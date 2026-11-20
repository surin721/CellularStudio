# Selects a sidebar page in the running app via UI Automation (dev helper for screenshots).
param([string]$Page)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$p = Get-Process CellularStudio | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$cond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Page)))
$el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if (-not $el) {
    # Messages radio has composite content; fall back to matching any radio containing the text
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton)))
    foreach ($a in $all) {
        $t = $a.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Page)))
        if ($t) { $el = $a; break }
    }
}
if (-not $el) { throw "nav item $Page not found" }
$el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
