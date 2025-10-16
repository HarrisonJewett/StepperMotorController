// App.xaml.cs
namespace StepperMotorController;

public partial class App : Microsoft.Maui.Controls.Application
{
    public App() => InitializeComponent();

    protected override Window CreateWindow(IActivationState? activationState)
        => new Window(new MainPage());
}
