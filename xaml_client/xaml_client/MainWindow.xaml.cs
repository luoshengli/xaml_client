using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace xaml_client
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void btn_load_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                tb_info.Text = string.Empty;
                b_result.Child = null;
                b_result.Resources.Clear();

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
                    // 预览容器是 Border，不能直接把 Window 作为 Child。
                    // 将 Window.Content 放入 Border，同时保留 Window.Resources。
                    if (!(window.Content is UIElement content))
                    {
                        throw new InvalidOperationException("Window 没有可显示的 Content。");
                    }

                    b_result.Resources = window.Resources;
                    b_result.Child = content;
                }
                else if (root is UIElement element)
                {
                    b_result.Resources = root.Resources;
                    b_result.Child = element;
                }
                else
                {
                    throw new InvalidOperationException("XAML 根元素不是 UIElement。");
                }

                tb_info.Text = "预览成功";
            }
            catch (Exception ex)
            {
                tb_info.Text = "预览失败：" + ex.Message;
            }
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
    }
}
