using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Pro2Plus;

public partial class BluetoothRestartWindow : Window
{
    public RadioRestartTarget? SelectedTarget { get; private set; }
    public BluetoothRestartWindow(IReadOnlyList<RadioRestartTarget> targets)
    {
        InitializeComponent();
        DataContext = targets;
        if (targets.Count == 1) AdapterPicker.SelectedIndex = 0;
        else SelectedDeviceText.Text = "有多个适配器，请自行选择；系统默认设备不代表 Pro2 的连接归属。";
    }
    private void AdapterPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConfirmButton == null || SelectedDeviceText == null) return;
        var target = AdapterPicker.SelectedItem as RadioRestartTarget;
        ConfirmButton.IsEnabled = target?.CanRestart == true;
        SelectedDeviceText.Text = target == null ? "请选择适配器。" : target.CanRestart ? target.PnpId : "无法核实此适配器的安全重启目标；不会重启其他设备。";
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterPicker.SelectedItem is not RadioRestartTarget target || !target.CanRestart) return;
        SelectedTarget = target;
        DialogResult = true;
    }
}
