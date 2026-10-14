$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Run this check with Windows PowerShell -STA.'
}

# Load the real XAML in WPF without compiling or starting AIMaster services.
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Data
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$mainXaml = [IO.File]::ReadAllText((Join-Path $repository 'AiManagerWindow.xaml'))
$namespaces = New-Object Xml.XmlNamespaceManager($mainXaml.NameTable)
$namespaces.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$resourceNodes = $mainXaml.SelectSingleNode('/p:Window/p:Window.Resources', $namespaces)
$resourceXaml = '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">' + $resourceNodes.InnerXml + '</ResourceDictionary>'

[xml]$aboutXaml = [IO.File]::ReadAllText((Join-Path $repository 'AboutWindow.xaml'))
$aboutXaml.DocumentElement.RemoveAttribute('Class', 'http://schemas.microsoft.com/winfx/2006/xaml')
foreach ($node in $aboutXaml.SelectNodes('//*[@Click or @RequestNavigate]')) {
    $node.RemoveAttribute('Click')
    $node.RemoveAttribute('RequestNavigate')
}
$sourceXaml = $aboutXaml.OuterXml
$bindingErrors = New-Object 'Collections.Generic.List[Exception]'
$dispatcher = [Windows.Threading.Dispatcher]::CurrentDispatcher
$onException = [Windows.Threading.DispatcherUnhandledExceptionEventHandler] {
    param($sender, $eventArgs)
    $bindingErrors.Add($eventArgs.Exception)
    $eventArgs.Handled = $true
}
$dispatcher.add_UnhandledException($onException)

function Open-AboutXaml([string]$Xaml, [string]$Language, [bool]$Light) {
    $bindingErrors.Clear()
    $window = [Windows.Markup.XamlReader]::Parse($Xaml)
    $window.Resources.MergedDictionaries.Add([Windows.Markup.XamlReader]::Parse($resourceXaml))
    if ($Light) {
        $window.Resources['AppBackground'] = [Windows.Media.Brushes]::WhiteSmoke
        $window.Resources['Ink'] = [Windows.Media.Brushes]::Black
        $window.Resources['Muted'] = [Windows.Media.Brushes]::DimGray
        $window.Resources['Panel'] = [Windows.Media.Brushes]::White
        $window.Resources['Signal'] = [Windows.Media.Brushes]::SeaGreen
    }

    # Read-only columns reproduce the same binding restriction as the
    # production anonymous object's getter-only properties.
    $table = New-Object Data.DataTable
    $fields = @('TitleText', 'VersionText', 'Description', 'StudioText', 'LicenseText',
        'LinksHeading', 'WebsiteText', 'RepositoryText', 'CommunityHeading', 'CommunityText', 'CloseText')
    foreach ($field in $fields) { $null = $table.Columns.Add($field, [string]) }
    $row = $table.NewRow()
    foreach ($field in $fields) { $row[$field] = "$Language $field" }
    $table.Rows.Add($row)
    foreach ($column in $table.Columns) { $column.ReadOnly = $true }
    $window.DataContext = $table.DefaultView[0]
    $window.Opacity = 0
    $window.ShowActivated = $false
    try {
        $window.Show()
        $window.UpdateLayout()
        $dispatcher.Invoke([Action] {}, [Windows.Threading.DispatcherPriority]::SystemIdle)
        if ($bindingErrors.Count -eq 0) {
            $linkRuns = @($window.Content.Children[0].Content.Children[4].Child.Children |
                Where-Object { $_ -is [Windows.Controls.TextBlock] } |
                ForEach-Object { $_.Inlines } |
                Where-Object { $_ -is [Windows.Documents.Hyperlink] } |
                ForEach-Object { $_.Inlines } |
                Where-Object { $_ -is [Windows.Documents.Run] })
            if ($linkRuns.Count -ne 3) { throw 'Expected all three rendered hyperlinks.' }
            $expected = @("$Language WebsiteText", "$Language RepositoryText", "$Language CommunityText")
            for ($index = 0; $index -lt $linkRuns.Count; $index++) {
                if ($linkRuns[$index].Text -cne $expected[$index]) { throw 'A hyperlink label did not bind.' }
            }
        }
    } catch {
        $bindingErrors.Add($_.Exception)
    } finally {
        $window.Close()
        $dispatcher.Invoke([Action] {}, [Windows.Threading.DispatcherPriority]::SystemIdle)
        $table.Dispose()
    }
}

try {
    $oldXaml = $sourceXaml.Replace(', Mode=OneWay', '')
    Open-AboutXaml $oldXaml 'en-US' $false
    if (-not ($bindingErrors | Where-Object { $_.ToString() -match 'WebsiteText' })) {
        throw 'The regression check did not reproduce the original read-only binding failure.'
    }
    Write-Host 'PASS: original XAML reproduces the WebsiteText binding exception.'
    foreach ($language in @('zh-CN', 'en-US')) {
        foreach ($light in @($false, $true)) {
            Open-AboutXaml $sourceXaml $language $light
            if ($bindingErrors.Count -gt 0) { throw $bindingErrors[0] }
            Write-Host "PASS: corrected about XAML opens and renders all links ($language, light=$light)."
        }
    }
} finally {
    $dispatcher.remove_UnhandledException($onException)
}
