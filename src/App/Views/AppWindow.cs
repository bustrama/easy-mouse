using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace EasyMouse.App.Views;

/// <summary>Base for the app's windows: dark frame, language-driven flow direction, working caption buttons.</summary>
public class AppWindow : Window
{
    public AppWindow()
    {
        SetResourceReference(StyleProperty, "AppWindowStyle");
        SetBinding(FlowDirectionProperty, new Binding(nameof(Loc.FlowDirection)) { Source = Loc.Instance });
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => Close()));
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => WindowState = WindowState.Minimized));
    }
}
