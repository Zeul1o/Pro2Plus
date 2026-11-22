using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace Pro2Plus.UI.Pages;

public partial class ConnectPage : UserControl
{
    public ConnectPage()
    {
        InitializeComponent();
    }

    /// <summary>用资源管理器打开本地日志文件夹（程序启动已建目录，这里兜底再建一次）。</summary>
    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        string folder = MainViewModel.LogFolderPath;
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + folder + "\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("打开日志目录失败：" + ex.Message + "\n\n路径：" + folder,
                "PRO2 助手", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
}
