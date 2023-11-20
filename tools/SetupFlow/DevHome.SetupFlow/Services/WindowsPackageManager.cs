﻿// Copyright (c) Microsoft Corporation and Contributors
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DevHome.Common.Exceptions;
using DevHome.Common.Services;
using DevHome.Services;
using DevHome.SetupFlow.Common.Extensions;
using DevHome.SetupFlow.Common.Helpers;
using DevHome.SetupFlow.Common.WindowsPackageManager;
using DevHome.SetupFlow.Exceptions;
using DevHome.SetupFlow.Models;
using Microsoft.Management.Deployment;
using Windows.Win32.Foundation;

namespace DevHome.SetupFlow.Services;

/// <summary>
/// Windows package manager class is an entry point for using the WinGet COM API.
/// </summary>
public class WindowsPackageManager : IWindowsPackageManager, IDisposable
{
    public const int AppInstallerErrorFacility = 0xA15;
    public const string AppInstallerProductId = "9NBLGGH4NNS1";
    public const string AppInstallerPackageFamilyName = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";

    // COM error codes
    public const int RpcServerUnavailable = unchecked((int)0x800706BA);
    public const int RpcCallFailed = unchecked((int)0x800706BE);

    // Package manager URI constants:
    // - x-ms-winget: is a custom scheme for WinGet package manager
    // - winget: is a reserved URI name for the winget catalog
    public const string Scheme = "x-ms-winget";
    public const string WingetCatalogURIName = "winget";

    private readonly WindowsPackageManagerFactory _wingetFactory;
    private readonly IAppInstallManagerService _appInstallManagerService;
    private readonly IPackageDeploymentService _packageDeploymentService;

    // Catalogs locks
    private readonly SemaphoreSlim _searchCatalogLock = new (1, 1);
    private readonly SemaphoreSlim _wingetCatalogLock = new (1, 1);
    private bool _disposedValue;

    // Catalogs
    private Microsoft.Management.Deployment.PackageCatalog _searchCatalog;
    private Microsoft.Management.Deployment.PackageCatalog _wingetCatalog;

    public WindowsPackageManager(
        WindowsPackageManagerFactory wingetFactory,
        IAppInstallManagerService appInstallManagerService,
        IPackageDeploymentService packageDeploymentService)
    {
        _wingetFactory = wingetFactory;
        _appInstallManagerService = appInstallManagerService;
        _packageDeploymentService = packageDeploymentService;
    }

    public string WinGetCatalogId { get; private set; }

    public string MsStoreId { get; private set; }

    public bool CanSearch => _searchCatalog != null;

    public async Task InitializeAsync()
    {
        await Task.Run(async () =>
        {
            // Create and connect to catalogs
            _searchCatalog = await CreateAndConnectSearchCatalogAsync();
            _wingetCatalog = await CreateAndConnectWinGetCatalogAsync();

            // Extract catalog ids for predefined catalogs
            WinGetCatalogId = GetPredefinedCatalogId(PredefinedPackageCatalog.OpenWindowsCatalog);
            MsStoreId = GetPredefinedCatalogId(PredefinedPackageCatalog.MicrosoftStore);
        });
    }

    public async Task<InstallPackageResult> InstallPackageAsync(WinGetPackage package, Guid activityId)
    {
        return await Task.Run(async () =>
        {
            var packageManager = _wingetFactory.CreatePackageManager();
            var options = _wingetFactory.CreateInstallOptions();
            options.PackageInstallMode = PackageInstallMode.Silent;

            var catalog = packageManager.GetPackageCatalogByName(package.CatalogName);
            var result = await catalog.ConnectAsync();

            var op = _wingetFactory.CreateFindPackagesOptions();
            var mop = _wingetFactory.CreatePackageMatchFilter();
            mop.Option = PackageFieldMatchOption.Equals;
            mop.Field = PackageMatchField.Id;
            mop.Value = package.Id;
            op.Filters.Add(mop);
            var mathc = await result.PackageCatalog.FindPackagesAsync(op);
            if (mathc.Matches.Count == 0)
            {
                return new InstallPackageResult
                {
                    ExtendedErrorCode = 0,
                    RebootRequired = false,
                };
            }

            Log.Logger?.ReportInfo(Log.Component.AppManagement, $"Starting package install for {package.Id}");
            var installResult = await packageManager.InstallPackageAsync(mathc.Matches[0].CatalogPackage, options).AsTask();
            var extendedErrorCode = installResult.ExtendedErrorCode?.HResult ?? HRESULT.S_OK;

            // Contract version 4
            var installErrorCode = installResult.GetValueOrDefault(res => res.InstallerErrorCode, HRESULT.S_OK);

            Log.Logger?.ReportInfo(
                Log.Component.AppManagement,
                $"Install result: Status={installResult.Status}, InstallerErrorCode={installErrorCode}, ExtendedErrorCode={extendedErrorCode}, RebootRequired={installResult.RebootRequired}");

            if (installResult.Status != InstallResultStatus.Ok)
            {
                throw new InstallPackageException(installResult.Status, extendedErrorCode, installErrorCode);
            }

            return new InstallPackageResult()
            {
                ExtendedErrorCode = extendedErrorCode,
                RebootRequired = installResult.RebootRequired,
            };
        });
    }

    public async Task<bool> IsAppInstallerUpdateAvailableAsync()
    {
        try
        {
            Log.Logger?.ReportInfo(Log.Component.AppManagement, "Checking if AppInstaller has an update ...");
            var appInstallerUpdateAvailable = await _appInstallManagerService.IsAppUpdateAvailableAsync(AppInstallerProductId);
            Log.Logger?.ReportInfo(Log.Component.AppManagement, $"AppInstaller update available = {appInstallerUpdateAvailable}");
            return appInstallerUpdateAvailable;
        }
        catch (Exception e)
        {
            Log.Logger?.ReportError(Log.Component.AppManagement, "Failed to check if AppInstaller has an update, defaulting to false", e);
            return false;
        }
    }

    public async Task<bool> StartAppInstallerUpdateAsync()
    {
        try
        {
            Log.Logger?.ReportInfo(Log.Component.AppManagement, "Starting AppInstaller update ...");
            var updateStarted = await _appInstallManagerService.StartAppUpdateAsync(AppInstallerProductId);
            Log.Logger?.ReportInfo(Log.Component.AppManagement, $"Start AppInstaller update = {updateStarted}");
            return updateStarted;
        }
        catch (Exception e)
        {
            Log.Logger?.ReportError(Log.Component.AppManagement, "Failed to start AppInstaller update", e);
            return false;
        }
    }

    public async Task<bool> RegisterAppInstallerAsync()
    {
        try
        {
            Log.Logger?.ReportInfo(Log.Component.AppManagement, "Starting AppInstaller registration ...");
            await _packageDeploymentService.RegisterPackageForCurrentUserAsync(AppInstallerPackageFamilyName);
            Log.Logger?.ReportInfo(Log.Component.AppManagement, $"AppInstaller registered successfully");
            return true;
        }
        catch (RegisterPackageException e)
        {
            Log.Logger?.ReportError(Log.Component.AppManagement, $"Failed to register AppInstaller", e);
            return false;
        }
        catch (Exception e)
        {
            Log.Logger?.ReportError(Log.Component.AppManagement, "An unexpected error occurred when registering AppInstaller", e);
            return false;
        }
    }

    public async Task<IList<IWinGetPackage>> GetPackagesAsync(ISet<Uri> packageUriSet)
    {
        return await Task.Run(async () =>
        {
            // TODO Add support for other catalogs (e.g. `msstore` and custom).
            // https://github.com/microsoft/devhome/issues/1521
            HashSet<string> wingetPackageIds = new ();
            foreach (var packageUri in packageUriSet)
            {
                if (TryGetPackageId(packageUri, out var packageId))
                {
                    wingetPackageIds.Add(packageId);
                }
                else
                {
                    Log.Logger?.ReportWarn(Log.Component.AppManagement, $"Failed to get package id from uri '{packageUri}'");
                }
            }

            return await GetPackagesAsync(_wingetCatalog, wingetPackageIds);
        });
    }

    /// <summary>
    /// Gets the id of the provided predefined catalog
    /// </summary>
    /// <param name="catalog">Predefined catalog</param>
    /// <returns>Catalog id</returns>
    private string GetPredefinedCatalogId(PredefinedPackageCatalog catalog)
    {
        var packageManager = _wingetFactory.CreatePackageManager();
        var packageCatalog = packageManager.GetPredefinedPackageCatalog(catalog);
        return packageCatalog.Info.Id;
    }

    /// <summary>
    /// Check if WindowsPackageManager COM Server is available by creating a
    /// dummy out-of-proc object
    /// </summary>
    /// <returns>True if server is available, false otherwise.</returns>
    public async Task<bool> IsCOMServerAvailableAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                Log.Logger?.ReportInfo(Log.Component.AppManagement, $"Attempting to create a dummy out-of-proc {nameof(PackageManager)} COM object to test if the COM server is available");
                _wingetFactory.CreatePackageManager();
                Log.Logger?.ReportInfo(Log.Component.AppManagement, $"WinGet COM Server is available");
            });

            return true;
        }
        catch (Exception e)
        {
            Log.Logger?.ReportError(Log.Component.AppManagement, $"Failed to create dummy {nameof(PackageManager)} COM object. WinGet COM Server is not available.", e);
            return false;
        }
    }

    public async Task<IList<IWinGetPackage>> SearchAsync(string query, uint limit = 0)
    {
        return await DoWithRecovery(async () =>
        {
            return await Task.Run(async () =>
            {
                await _searchCatalogLock.WaitAsync();
                try
                {
                    // Use default filter criteria for searching
                    Log.Logger?.ReportInfo(Log.Component.AppManagement, $"Searching for '{query}'. Result limit: {limit}");
                    var options = _wingetFactory.CreateFindPackagesOptions();
                    var filter = _wingetFactory.CreatePackageMatchFilter();
                    filter.Field = PackageMatchField.CatalogDefault;
                    filter.Option = PackageFieldMatchOption.ContainsCaseInsensitive;
                    filter.Value = query;
                    options.Selectors.Add(filter);
                    options.ResultLimit = limit;

                    return await FindPackagesAsync(_searchCatalog, options);
                }
                catch (Exception e)
                {
                    Log.Logger?.ReportError(Log.Component.AppManagement, $"Error searching for packages.", e);
                    throw;
                }
                finally
                {
                    _searchCatalogLock.Release();
                }
            });
        });
    }

    public async Task<IList<IWinGetPackage>> GetPackagesAsync(Microsoft.Management.Deployment.PackageCatalog catalog, ISet<string> packageIdSet)
    {
        return await Task.Run(async () =>
        {
            await _wingetCatalogLock.WaitAsync();
            try
            {
                // Skip search if set is empty
                if (!packageIdSet.Any())
                {
                    Log.Logger?.ReportWarn(Log.Component.AppManagement, $"{nameof(GetPackagesAsync)} received an empty set of package id. Skipping search.");
                    return new List<IWinGetPackage>();
                }

                Log.Logger?.ReportInfo(Log.Component.AppManagement, $"Getting package set from catalog {catalog.Info.Name}");
                var options = _wingetFactory.CreateFindPackagesOptions();
                foreach (var packageId in packageIdSet)
                {
                    Log.Logger?.ReportInfo(Log.Component.AppManagement, $"Adding package [{packageId}] to query");
                    var filter = _wingetFactory.CreatePackageMatchFilter();
                    filter.Field = PackageMatchField.Id;
                    filter.Option = PackageFieldMatchOption.Equals;
                    filter.Value = packageId;
                    options.Selectors.Add(filter);
                }

                Log.Logger?.ReportInfo(Log.Component.AppManagement, "Starting search for packages");
                return await FindPackagesAsync(catalog, options);
            }
            catch (Exception e)
            {
                Log.Logger?.ReportError(Log.Component.AppManagement, $"Error getting packages.", e);
                throw;
            }
            finally
            {
               _wingetCatalogLock.Release();
            }
        });
    }

    /// <summary>
    /// Try get the package id from a package uri
    /// </summary>
    /// <param name="packageUri">Input package uri</param>
    /// <param name="packageId">Output package id</param>
    /// <returns>True if the package uri is valid and a package id was identified, false otherwise.</returns>
    private bool TryGetPackageId(Uri packageUri, out string packageId)
    {
        // TODO Add support for other catalogs (e.g. `msstore` and custom).
        // https://github.com/microsoft/devhome/issues/1521
        if (packageUri.Scheme == Scheme &&
            packageUri.Host == WingetCatalogURIName &&
            packageUri.Segments.Length == 2)
        {
            packageId = packageUri.Segments[1];
            return true;
        }

        packageId = null;
        return false;
    }

    private async Task<Microsoft.Management.Deployment.PackageCatalog> CreateAndConnectSearchCatalogAsync()
    {
        await _searchCatalogLock.WaitAsync();
        try
        {
            var packageManager = _wingetFactory.CreatePackageManager();
            var catalogs = packageManager.GetPackageCatalogs();
            return await CreateAndConnectCatalogAsync(catalogs);
        }
        finally
        {
            _searchCatalogLock.Release();
        }
    }

    private async Task<Microsoft.Management.Deployment.PackageCatalog> CreateAndConnectWinGetCatalogAsync()
    {
        await _wingetCatalogLock.WaitAsync();
        try
        {
            var packageManager = _wingetFactory.CreatePackageManager();
            var catalog = packageManager.GetPredefinedPackageCatalog(PredefinedPackageCatalog.OpenWindowsCatalog);
            return await CreateAndConnectCatalogAsync(new List<PackageCatalogReference>() { catalog });
        }
        finally
        {
            _wingetCatalogLock.Release();
        }
    }

    private async Task<Microsoft.Management.Deployment.PackageCatalog> CreateAndConnectCatalogAsync(IReadOnlyList<PackageCatalogReference> catalogReferences)
    {
        // Search in all catalogs including the local catalog which allows detecting if a package is installed
        var compositeCatalogOptions = _wingetFactory.CreateCreateCompositePackageCatalogOptions();
        compositeCatalogOptions.CompositeSearchBehavior = CompositeSearchBehavior.RemotePackagesFromAllCatalogs;

        // Add all catalogs to the new composite catalog
        // Note: Cannot use foreach or LINQ for out-of-process IVector
        // Bug: https://github.com/microsoft/CsWinRT/issues/1205
        var count = catalogReferences.Count;
        for (var i = 0; i < count; ++i)
        {
            compositeCatalogOptions.Catalogs.Add(catalogReferences[i]);
        }

        // Create and connect the new composite catalog
        var packageManager = _wingetFactory.CreatePackageManager();
        var disconnectedCatalog = packageManager.CreateCompositePackageCatalog(compositeCatalogOptions);
        var connectResult = await disconnectedCatalog.ConnectAsync();
        if (connectResult.Status == ConnectResultStatus.Ok)
        {
            return connectResult.PackageCatalog;
        }

        Log.Logger?.ReportError(Log.Component.AppManagement, $"Failed to connect to catalog with status {connectResult.Status}");
        return null;
    }

    /// <summary>
    /// Core method for finding packages based on the provided options
    /// </summary>
    /// <param name="options">Find packages options</param>
    /// <returns>List of winget package matches</returns>
    /// <exception cref="InvalidOperationException">Exception thrown if the catalog is not connected before attempting to find packages</exception>
    /// <exception cref="FindPackagesException">Exception thrown if the find packages operation failed</exception>
    private async Task<IList<IWinGetPackage>> FindPackagesAsync(Microsoft.Management.Deployment.PackageCatalog catalog, FindPackagesOptions options)
    {
        Log.Logger?.ReportInfo(Log.Component.AppManagement, "Performing search");
        var result = new List<IWinGetPackage>();
        var findResult = await catalog.FindPackagesAsync(options);
        if (findResult.Status != FindPackagesResultStatus.Ok)
        {
            Log.Logger?.ReportError(Log.Component.AppManagement, $"Failed to find packages with status {findResult.Status}");
            throw new FindPackagesException(findResult.Status);
        }

        Log.Logger?.ReportInfo(Log.Component.AppManagement, $"Found {findResult.Matches} results");

        // Cannot use foreach or LINQ for out-of-process IVector
        // Bug: https://github.com/microsoft/CsWinRT/issues/1205
        for (var i = 0; i < findResult.Matches.Count; ++i)
        {
            var catalogPackage = findResult.Matches[i].CatalogPackage;
            Log.Logger?.ReportInfo(Log.Component.AppManagement, $"Found [{catalogPackage.Id}]");
            var installOptions = _wingetFactory.CreateInstallOptions();
            installOptions.PackageInstallScope = PackageInstallScope.Any;
            result.Add(new WinGetPackage(catalogPackage, installOptions));
        }

        return result;
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                // _connectionLock.Dispose();
            }

            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private async Task<T> DoWithRecovery<T>(Func<Task<T>> actionFunc)
    {
        const int maxRetries = 5;
        const int delayMs = 2_000;
        var retry = 0;
        while (retry <= maxRetries)
        {
            try
            {
                return await actionFunc();
            }
            catch (COMException e) when ((e.HResult == RpcServerUnavailable || e.HResult == RpcCallFailed) && retry < maxRetries)
            {
                // Retry with exponential backoff
                var backoffMs = delayMs * (int)Math.Pow(2, retry++);
                Log.Logger?.ReportError(
                    Log.Component.AppManagement,
                    $"Failed to operate on out-of-proc object with error code: 0x{e.HResult:x}. Attempting to recover in: {backoffMs} ms");
                await Task.Delay(TimeSpan.FromMilliseconds(backoffMs));

                try
                {
                    Log.Logger?.ReportInfo(Log.Component.AppManagement, $"Attempting to re-initialize ...");
                    await InitializeAsync();
                    Log.Logger?.ReportInfo(Log.Component.AppManagement, $"Re-initialize succeeded. Attempting to resume original task");
                }
                catch
                {
                    Log.Logger?.ReportError(Log.Component.AppManagement, $"Initialization failed on retry number: {retry + 1}");
                }
            }
        }

        throw new PackageManagerRecoveryTimeoutException();
    }

    private async Task DoWithRecovery(Func<Task> actionFunc)
    {
        const int voidValue = 0;
        _ = await DoWithRecovery<int>(async () =>
        {
            await actionFunc();
            return voidValue;
        });
    }

    public class PackageManagerRecoveryTimeoutException : Exception
    {
        public PackageManagerRecoveryTimeoutException()
        {
        }
    }
}
