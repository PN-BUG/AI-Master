// 全局 using - 显式声明避免 WPF/WinForms 冲突
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;

// WPF 别名 (与 WinForms 冲突的类型)
global using WpfApplication = System.Windows.Application;
global using WpfButton = System.Windows.Controls.Button;
global using WpfBrush = System.Windows.Media.Brush;
global using WpfBrushes = System.Windows.Media.Brushes;
global using WpfColor = System.Windows.Media.Color;
global using WpfMessageBox = System.Windows.MessageBox;
global using WpfOrientation = System.Windows.Controls.Orientation;
global using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
global using WpfVerticalAlignment = System.Windows.VerticalAlignment;
global using WpfCursors = System.Windows.Input.Cursors;
global using WpfBinding = System.Windows.Data.Binding;
global using WpfRelativeSource = System.Windows.Data.RelativeSource;
global using WpfRelativeSourceMode = System.Windows.Data.RelativeSourceMode;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AIMaster.Smoke")]
