// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Runtime.InteropServices;
using DevHome.EnvironmentVariables.Win32;
using DevHome.Telemetry;
using EnvironmentVariablesUILib;
using EnvironmentVariablesUILib.Helpers;
using EnvironmentVariablesUILib.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using WinUIEx;

namespace DevHome.EnvironmentVariables;

public sealed partial class EnvironmentVariablesMainWindow : WindowEx
{
    private EnvironmentVariablesMainPage EnvVariablesUtilityMainPage { get; }

    private string UtilityTitle { get; set; }

    private readonly Guid activityId;

    private readonly Serilog.ILogger _log = Log.ForContext("SourceContext", nameof(EnvironmentVariablesApp));

    public EnvironmentVariablesMainWindow(Guid activityId)
    {
        this.activityId = activityId;
        this.InitializeComponent();

        Activated += MainWindow_Activated;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var loader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader("PowerToys.EnvironmentVariablesUILib.pri", "PowerToys.EnvironmentVariablesUILib/Resources");
        var title = EnvironmentVariablesApp.GetService<IElevationHelper>().IsElevated ? loader.GetString("WindowAdminTitle") : loader.GetString("WindowTitle");
        UtilityTitle = title;

        Title = UtilityTitle;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets/EnvironmentVariables/EnvironmentVariables.ico"));

        var handle = this.GetWindowHandle();
        RegisterWindow(handle);

        EnvVariablesUtilityMainPage = EnvironmentVariablesApp.GetService<EnvironmentVariablesMainPage>();

        TelemetryFactory.Get<ITelemetry>().Log("EnvironmentVariablesApp_EnvironmentVariablesMainWindow_Initialized", LogLevel.Measure, new EmptyEvent(), this.activityId);
        _log.Information("EnvironmentVariablesApp MainWindow Intialized");
    }

    private void Grid_Loaded(object sender, RoutedEventArgs e)
    {
        MainGrid.Children.Add(EnvVariablesUtilityMainPage);
        Grid.SetRow(EnvVariablesUtilityMainPage, 1);

        TelemetryFactory.Get<ITelemetry>().Log("EnvironmentVariablesApp_EnvironmentVariablesMainWindow_GridLoaded", LogLevel.Measure, new EmptyEvent(), this.activityId);
        _log.Information("EnvironmentVariablesApp MainWindow Grid loaded");
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        AppTitleBar.IsActive = args.WindowActivationState != WindowActivationState.Deactivated;
    }

    private static readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    private static NativeMethods.WinProc newWndProc;
    private static IntPtr oldWndProc = IntPtr.Zero;

    private void RegisterWindow(IntPtr handle)
    {
        newWndProc = new NativeMethods.WinProc(WndProc);

        oldWndProc = NativeMethods.SetWindowLongPtr(handle, NativeMethods.WindowLongIndexFlags.GWL_WNDPROC, newWndProc);
    }

    private static IntPtr WndProc(IntPtr hWnd, NativeMethods.WindowMessage msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WindowMessage.WM_SETTINGSCHANGED:
                {
                    var lParamStr = Marshal.PtrToStringUTF8(lParam);
                    if (lParamStr == "Environment")
                    {
                        if (wParam != (IntPtr)0x12345)
                        {
                            var viewModel = EnvironmentVariablesApp.GetService<MainViewModel>();
                            viewModel.EnvironmentState = EnvironmentVariablesUILib.Models.EnvironmentState.EnvironmentMessageReceived;
                        }
                    }

                    break;
                }

            default:
                break;
        }

        return NativeMethods.CallWindowProc(oldWndProc, hWnd, msg, wParam, lParam);
    }
}
