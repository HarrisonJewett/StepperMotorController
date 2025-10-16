using Microsoft.Maui;                   // MauiApp
using Microsoft.Maui.Hosting;           // MauiApp.CreateBuilder
using Microsoft.Maui.Controls.Hosting;  // UseMauiApp

namespace StepperMotorController;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        return builder.Build();
    }
}