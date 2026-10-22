using System.ComponentModel;
using Avalonia.Threading;
using RainExplorer.Models;
using RainExplorer.ViewModels;

namespace RainExplorer.Services;

public sealed class FileManagerIntegration : ObservableObject
{
    public static FileManagerIntegration Instance { get; } = new();
    private FileManagerService? _service;
    private Func<FileManagerRequest, Task>? _show;
    private string? _address;
    private bool _madeDefault;
    private int _revision;
    private string _status = "Off. Your current desktop handler stays in use.";
    public string Status { get => _status; private set => Set(ref _status, value); }
    private string _defaultStatus = "Choose Rain for opening folders and showing downloads in their folder.";
    public string DefaultStatus { get => _defaultStatus; private set => Set(ref _defaultStatus, value); }
    private bool _isMakingDefault;
    public bool IsMakingDefault { get => _isMakingDefault; private set => Set(ref _isMakingDefault, value); }

    private FileManagerIntegration() { }

    public void Start(Func<FileManagerRequest, Task> show, string? address = null)
    {
        _show = show;
        _address = address;
        SettingsStore.Instance.Settings.PropertyChanged += OnSettingsChanged;
        _ = UpdateAsync();
    }

    public void Stop()
    {
        SettingsStore.Instance.Settings.PropertyChanged -= OnSettingsChanged;
        _revision++;
        _service?.Dispose();
        _service = null;
        _show = null;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.HandleShowInFolder)) _ = UpdateAsync();
    }

    public Task RetryAsync() => _show is null ? Task.CompletedTask : UpdateAsync();

    public async Task MakeDefaultAsync()
    {
        if (IsMakingDefault) return;
        IsMakingDefault = true;
        DefaultStatus = "Making Rain your default file manager…";
        bool foldersChanged = false;
        try
        {
            FileManagerActivation.ValidateEnable();
            await DesktopFileManagerService.SetDefaultAsync();
            foldersChanged = true;
            _madeDefault = true;
            SettingsStore.Instance.Settings.HandleShowInFolder = true;
            await RetryAsync();
            var service = _service;
            if (service is null)
            {
                DefaultStatus = "Rain is the folder default. Show in folder needs attention below.";
                return;
            }
            if (!service.IsOwner)
            {
                await DesktopFileManagerService.ReleaseBackgroundDolphinAsync(_address);
                var until = DateTime.UtcNow.AddSeconds(2);
                while (!service.IsOwner && DateTime.UtcNow < until) await Task.Delay(50);
            }
            DefaultStatus = service.IsOwner
                ? "Rain is your default file manager for folders and Show in folder."
                : "Rain is the folder default. Close the other file manager to finish enabling Show in folder.";
        }
        catch (Exception ex)
        {
            DefaultStatus = foldersChanged ? $"Rain is the folder default. Show in folder: {ex.Message}"
                : $"Could not make Rain the default: {ex.Message}";
        }
        finally { IsMakingDefault = false; }
    }

    private async Task UpdateAsync()
    {
        int revision = ++_revision;
        _service?.Dispose();
        _service = null;
        bool enabled = SettingsStore.Instance.Settings.HandleShowInFolder;
        Status = enabled ? "Enabling Show in folder…" : "Off. Your current desktop handler stays in use.";
        if (_madeDefault && !enabled && !IsMakingDefault)
            DefaultStatus = "Rain is the folder default. Show in folder is turned off.";
        try
        {
            // Keep activation-file writes in order when the setting changes quickly.
            await ActivationGate.WaitAsync();
            try
            {
                if (revision != _revision) return;
                bool changed = await Task.Run(() =>
                {
                    if (enabled) { FileManagerActivation.Enable(); return true; }
                    return FileManagerActivation.Disable();
                });
                // A newly created service directory may not be watched by the running bus yet.
                if (changed) await DesktopFileManagerService.ReloadActivationAsync(_address);
            }
            finally { ActivationGate.Release(); }
            if (revision != _revision || !enabled || _show is null) return;
            var service = new FileManagerService(_show);
            _service = service;
            service.OwnershipChanged += owner => Dispatcher.UIThread.Post(() =>
            {
                if (revision != _revision) return;
                Status = OwnerStatus(owner);
                if (_madeDefault && owner && !IsMakingDefault)
                    DefaultStatus = "Rain is your default file manager for folders and Show in folder.";
            });
            await service.StartAsync(_address);
            if (revision != _revision) { service.Dispose(); return; }
            Status = OwnerStatus(service.IsOwner);
            _ = WatchDisconnect(service, revision);
        }
        catch (Exception ex)
        {
            if (revision != _revision) return;
            _service?.Dispose();
            _service = null;
            Status = $"Could not enable Show in folder: {ex.Message}";
        }
    }

    private static readonly SemaphoreSlim ActivationGate = new(1);

    private async Task WatchDisconnect(FileManagerService service, int revision)
    {
        await service.WaitForDisconnectAsync();
        Dispatcher.UIThread.Post(() =>
        {
            if (revision == _revision) Status = "The desktop bus disconnected. Use Retry to reconnect.";
        });
    }

    private static string OwnerStatus(bool owner) => owner
        ? "Active. Other apps can launch Rain to reveal local files, folders and properties."
        : "Waiting for another file manager to close. Rain will handle requests after it releases the desktop service.";
}
