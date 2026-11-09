namespace XSurLab.Audio;

public enum DeviceChangeKind { Added, Removed, State, Default, Format }
public sealed record DeviceChange(DeviceChangeKind Kind, string? DeviceId);

/// <summary>
/// Reports output endpoints being plugged in, removed, enabled/disabled, made default, or given a new Windows format.
/// <see cref="Changed"/> is raised on a Windows audio thread, often several times for one physical change: hand it to
/// the UI thread and debounce before enumerating again. Create and dispose on the same thread.
/// </summary>
public sealed class DeviceWatcher : IDisposable
{
    private readonly IDeviceEnumerator enumerator;
    private readonly NotificationClient client;
    private int disposed;
    public event EventHandler<DeviceChange>? Changed;

    public DeviceWatcher()
    {
        enumerator = (IDeviceEnumerator)new DeviceEnumeratorObject();
        client = new NotificationClient(Raise);
        try { Native.Check(enumerator.RegisterEndpointNotificationCallback(client)); }
        catch { Native.Release(enumerator); throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { enumerator.UnregisterEndpointNotificationCallback(client); }
        finally { Native.Release(enumerator); }
    }

    private void Raise(DeviceChangeKind kind, string? id)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        // A throwing subscriber must not unwind into the Windows audio service.
        try { Changed?.Invoke(this, new(kind, id)); } catch { }
    }
}

internal sealed class NotificationClient(Action<DeviceChangeKind, string?> raise) : IMMNotificationClient
{
    // The engine format and the friendly name are what the output list shows; other property changes are noise.
    internal static readonly Guid FormatKey = new("F19F064D-082C-4E27-BC73-6882A1BB8E4C"), NameKey = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");
    public int OnDeviceStateChanged(string id, uint state) { raise(DeviceChangeKind.State, id); return 0; }
    public int OnDeviceAdded(string id) { raise(DeviceChangeKind.Added, id); return 0; }
    public int OnDeviceRemoved(string id) { raise(DeviceChangeKind.Removed, id); return 0; }
    // Render flow (0) with the multimedia role (1): the default that EnumerateAsync marks.
    public int OnDefaultDeviceChanged(int flow, int role, string? id) { if (flow == 0 && role == 1) raise(DeviceChangeKind.Default, id); return 0; }
    public int OnPropertyValueChanged(string id, PropertyKey key)
    {
        if ((key.Format == FormatKey && key.Id == 0) || (key.Format == NameKey && key.Id == 14)) raise(DeviceChangeKind.Format, id);
        return 0;
    }
}

public static class DeviceList
{
    /// <summary>
    /// Makes <paramref name="target"/> equal to <paramref name="devices"/> with the fewest edits. Entries that did not change
    /// keep their instance and are never removed and re-added, so a list control does not lose its selection.
    /// </summary>
    public static void Sync<T>(IList<T> target, IReadOnlyList<T> devices)
    {
        var comparer = EqualityComparer<T>.Default;
        for (int i = target.Count - 1; i >= 0; i--)
            if (!devices.Contains(target[i], comparer)) target.RemoveAt(i);
        for (int i = 0; i < devices.Count; i++)
        {
            int at = -1;
            for (int j = i; j < target.Count; j++) if (comparer.Equals(target[j], devices[i])) { at = j; break; }
            if (at < 0) target.Insert(i, devices[i]);
            else if (at != i)
            {
                var item = target[at];
                if (target is System.Collections.ObjectModel.ObservableCollection<T> observable) observable.Move(at, i);
                else { target.RemoveAt(at); target.Insert(i, item); }
            }
        }
    }
}
