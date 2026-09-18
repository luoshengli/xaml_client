using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;

namespace xaml_client
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private EmbeddedWindowHost _embeddedWindowHost;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void btn_load_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                tb_info.Text = string.Empty;
                ClearPreview();

                var xaml = rb_xaml.Text;
                if (string.IsNullOrWhiteSpace(xaml))
                {
                    tb_info.Text = "请输入要预览的 XAML。";
                    return;
                }

                // x:Class 只用于编译型 XAML，运行时 XamlReader.Parse 不需要它。
                xaml = RemoveXClassAttribute(xaml);

                var root = XamlReader.Parse(xaml) as FrameworkElement;
                if (root == null)
                {
                    throw new InvalidOperationException("XAML 根元素不是可显示的 FrameworkElement。");
                }

                if (root is Window window)
                {
                    // B 方案：保留完整 Window，通过 Win32 SetParent 将 Window 的 HWND
                    // 重新设置为右侧预览区域的子窗口。这样 Window.Resources、Window.Content
                    // 以及 Content 内部的数据绑定/StaticResource 都继续由原 Window 持有。
                    _embeddedWindowHost = new EmbeddedWindowHost(window, message => tb_info.Text = message);
                    b_result.Child = _embeddedWindowHost;
                    tb_info.Text = "正在嵌入 Window...";
                }
                else if (root is UIElement element)
                {
                    b_result.Child = element;
                    tb_info.Text = "预览成功";
                }
                else
                {
                    throw new InvalidOperationException("XAML 根元素不是 UIElement。");
                }
            }
            catch (Exception ex)
            {
                ClearPreview();
                tb_info.Text = "预览失败：" + ex.Message;
            }
        }

        private void ClearPreview()
        {
            if (_embeddedWindowHost != null)
            {
                _embeddedWindowHost.Dispose();
                _embeddedWindowHost = null;
            }

            b_result.Child = null;
        }

        private static string RemoveXClassAttribute(string xaml)
        {
            // 支持 x:Class="..." 和 x:Class='...' 两种常见写法。
            const string pattern = @"\s+x:Class\s*=\s*(['""]).*?\1";
            return System.Text.RegularExpressions.Regex.Replace(
                xaml,
                pattern,
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.Singleline);
        }

        protected override void OnClosed(EventArgs e)
        {
            ClearPreview();
            base.OnClosed(e);
        }
    }

    /// <summary>
    /// 将一个真实的 WPF Window HWND 嵌入到 WPF 布局区域。
    ///
    /// WPF 本身不允许 Window 作为 Border.Child，因此这里不是把 Window
    /// 当成普通 Visual 添加，而是创建一个 HwndHost 作为 HWND 容器，
    /// 再通过 Win32 SetParent 将目标 Window 的 HWND 设为该容器的子窗口。
    /// </summary>
    internal sealed class EmbeddedWindowHost : HwndHost, IDisposable
    {
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_BORDER = 0x00800000;

        private const int GWL_STYLE = -16;

        private static readonly IntPtr HWND_TOP = new IntPtr(0);

        private readonly Window _window;
        private readonly Action<string> _errorCallback;
        private IntPtr _containerHandle;
        private IntPtr _windowHandle;
        private bool _disposed;

        public EmbeddedWindowHost(Window window, Action<string> errorCallback)
        {
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _errorCallback = errorCallback;

            // 这些属性只影响 Window 的顶层窗口外观，不会删除 Window.Content。
            // 预览区域不需要标题栏、边框和系统按钮。
            _window.WindowStyle = WindowStyle.None;
            _window.ResizeMode = ResizeMode.NoResize;
            _window.ShowInTaskbar = false;
            _window.ShowActivated = false;
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Opacity = 0;
            _window.Closed += Window_Closed;
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            _containerHandle = NativeMethods.CreateWindowEx(
                0,
                "static",
                string.Empty,
                WS_CHILD | WS_VISIBLE,
                0,
                0,
                1,
                1,
                hwndParent.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (_containerHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "无法创建 Window 嵌入容器，Win32 错误码：" +
                    Marshal.GetLastWin32Error());
            }

            // HwndHost 已经创建了自己的 HWND，等到 WPF 完成布局后再显示和重设父窗口。
            Dispatcher.BeginInvoke(new Action(AttachWindow));

            return new HandleRef(this, _containerHandle);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            DetachWindow();

            if (hwnd.Handle != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(hwnd.Handle);
            }

            _containerHandle = IntPtr.Zero;
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);

            if (_windowHandle != IntPtr.Zero)
            {
                ResizeEmbeddedWindow();
            }
        }

        private void AttachWindow()
        {
            if (_disposed || _containerHandle == IntPtr.Zero || _windowHandle != IntPtr.Zero)
            {
                return;
            }

            try
            {
                _window.Show();

                var helper = new WindowInteropHelper(_window);
                _windowHandle = helper.Handle;

                if (_windowHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Window 显示后没有获得有效 HWND。");
                }

                // 把顶层窗口改成真正的 WS_CHILD，然后挂到 HwndHost 的 HWND 下。
                var style = NativeMethods.GetWindowLongPtr(_windowHandle, GWL_STYLE).ToInt64();
                style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX |
                           WS_MAXIMIZEBOX | WS_SYSMENU | WS_BORDER);
                style |= WS_CHILD | WS_VISIBLE;

                NativeMethods.SetWindowLongPtr(
                    _windowHandle,
                    GWL_STYLE,
                    new IntPtr(style));

                NativeMethods.SetParent(_windowHandle, _containerHandle);
                if (NativeMethods.GetParent(_windowHandle) != _containerHandle)
                {
                    throw new InvalidOperationException(
                        "无法将 Window HWND 嵌入预览区域，Win32 错误码：" +
                        Marshal.GetLastWin32Error());
                }

                NativeMethods.SetWindowPos(
                    _windowHandle,
                    HWND_TOP,
                    0,
                    0,
                    Math.Max(1, (int)Math.Round(ActualWidth)),
                    Math.Max(1, (int)Math.Round(ActualHeight)),
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

                _window.Opacity = 1;
                ResizeEmbeddedWindow();
            }
            catch (Exception ex)
            {
                // 不要先把 _windowHandle 清零，否则 Dispose() 无法把已经 Show()
                // 的 Window 重新脱离父窗口并 Close，可能留下一个隐藏/孤立的顶层 HWND。
                _errorCallback?.Invoke("Window 嵌入失败：" + ex.Message);
                Dispose();
            }
        }

        private void ResizeEmbeddedWindow()
        {
            if (_windowHandle == IntPtr.Zero)
            {
                return;
            }

            var width = Math.Max(1, (int)Math.Round(ActualWidth * GetDpiScaleX()));
            var height = Math.Max(1, (int)Math.Round(ActualHeight * GetDpiScaleY()));

            NativeMethods.SetWindowPos(
                _windowHandle,
                HWND_TOP,
                0,
                0,
                width,
                height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        private double GetDpiScaleX()
        {
            var source = PresentationSource.FromVisual(this);
            return source != null && source.CompositionTarget != null
                ? source.CompositionTarget.TransformToDevice.M11
                : 1.0;
        }

        private double GetDpiScaleY()
        {
            var source = PresentationSource.FromVisual(this);
            return source != null && source.CompositionTarget != null
                ? source.CompositionTarget.TransformToDevice.M22
                : 1.0;
        }

        private void DetachWindow()
        {
            if (_windowHandle != IntPtr.Zero)
            {
                NativeMethods.SetParent(_windowHandle, IntPtr.Zero);
                _windowHandle = IntPtr.Zero;
            }

            if (_window.IsVisible)
            {
                _window.Close();
            }

            _window.Closed -= Window_Closed;
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _windowHandle = IntPtr.Zero;
        }

        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (disposing)
            {
                DetachWindow();
            }

            base.Dispose(disposing);
        }

        private static class NativeMethods
        {
            internal const uint SWP_NOACTIVATE = 0x0010;
            internal const uint SWP_SHOWWINDOW = 0x0040;

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr CreateWindowEx(
                int exStyle,
                string className,
                string windowName,
                int style,
                int x,
                int y,
                int width,
                int height,
                IntPtr parent,
                IntPtr menu,
                IntPtr instance,
                IntPtr param);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool DestroyWindow(IntPtr hwnd);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern IntPtr GetParent(IntPtr hwnd);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetWindowPos(
                IntPtr hwnd,
                IntPtr insertAfter,
                int x,
                int y,
                int width,
                int height,
                uint flags);

            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr",
                SetLastError = true)]
            private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

            [DllImport("user32.dll", EntryPoint = "GetWindowLong",
                SetLastError = true)]
            private static extern int GetWindowLong32(IntPtr hwnd, int index);

            [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr",
                SetLastError = true)]
            private static extern IntPtr SetWindowLongPtr64(
                IntPtr hwnd,
                int index,
                IntPtr newValue);

            [DllImport("user32.dll", EntryPoint = "SetWindowLong",
                SetLastError = true)]
            private static extern int SetWindowLong32(
                IntPtr hwnd,
                int index,
                int newValue);

            internal static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
            {
                return IntPtr.Size == 8
                    ? GetWindowLongPtr64(hwnd, index)
                    : new IntPtr(GetWindowLong32(hwnd, index));
            }

            internal static IntPtr SetWindowLongPtr(
                IntPtr hwnd,
                int index,
                IntPtr newValue)
            {
                return IntPtr.Size == 8
                    ? SetWindowLongPtr64(hwnd, index, newValue)
                    : new IntPtr(SetWindowLong32(hwnd, index, newValue));
            }
        }
    }
}
