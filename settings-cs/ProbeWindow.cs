using System;
using System.Windows;

namespace RunEverSettings
{
    /// <summary>
    /// 工具链探针：验证 .NET Framework 4.8 + WPF 能编译运行。
    /// 编译通过即可删掉本文件。
    /// </summary>
    public class ProbeWindow : Window
    {
        public ProbeWindow()
        {
            this.Title = "RunEver 设置 · 工具链探针";
            this.Width = 420;
            this.Height = 180;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var text = new System.Windows.Controls.TextBlock
            {
                Text = "工具链验证\n.NET Framework 4.8 + WPF\n\n"
                     + "运行时版本：" + Environment.Version + "\n"
                     + "OS 版本：" + Environment.OSVersion,
                Margin = new Thickness(20),
                FontSize = 13
            };
            this.Content = text;
        }
    }
}
