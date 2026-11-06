using System.Security.Cryptography;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Worker;

internal sealed class SecretSlotStore : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, SecretSlot> _slots = [];
    private bool _disposed;

    public (Guid Id, DateTimeOffset ExpiresAtUtc) Create()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            SweepExpiredNoLock();
            if (_slots.Count >= ProtocolConstants.MaximumSecretSlots)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ResourceLimit,
                    "secret.slot_limit",
                    "The worker has reached its limit for pending secret slots.");
            }

            var id = Guid.NewGuid();
            var expires = DateTimeOffset.UtcNow.Add(Lifetime);
            _slots.Add(id, new SecretSlot(expires));

            return (id, expires);
        }
    }

    public void Store(Guid id, byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length > ProtocolConstants.MaximumSecretBytes)
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "secret.too_large",
                "The secret exceeded the protocol size limit.");
        }

        lock (_gate)
        {
            if (_disposed
                || !_slots.TryGetValue(id, out var slot)
                || slot.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                CryptographicOperations.ZeroMemory(secret);
                if (_slots.Remove(id, out var expired))
                {
                    expired.Dispose();
                }

                throw InvalidSlot();
            }

            slot.Set(secret);
        }
    }

    public byte[] Consume(Guid? id)
    {
        if (id is null)
        {
            return [];
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_slots.Remove(id.Value, out var slot))
            {
                throw InvalidSlot();
            }

            return slot.Take();
        }
    }

    public bool Discard(Guid id)
    {
        lock (_gate)
        {
            if (_disposed || !_slots.Remove(id, out var slot))
            {
                return false;
            }

            slot.Dispose();
            return true;
        }
    }

    private static ArchiveProviderException InvalidSlot() =>
        new(
            ArchiveErrorCategory.InvalidRequest,
            "secret.invalid_slot",
            "The secret slot was unknown, expired, empty, or already consumed.");

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var slot in _slots.Values)
            {
                slot.Dispose();
            }

            _slots.Clear();
        }
    }

    private void SweepExpiredNoLock()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _slots.ToArray())
        {
            if (pair.Value.ExpiresAtUtc <= now && _slots.Remove(pair.Key, out var expired))
            {
                expired.Dispose();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class SecretSlot : IDisposable
    {
        private byte[]? _value;

        public SecretSlot(DateTimeOffset expiresAtUtc)
        {
            ExpiresAtUtc = expiresAtUtc;
        }

        public DateTimeOffset ExpiresAtUtc { get; }

        public void Set(byte[] value)
        {
            if (Interlocked.CompareExchange(ref _value, value, null) is not null)
            {
                CryptographicOperations.ZeroMemory(value);
                throw new InvalidOperationException("Secret slot was already populated.");
            }
        }

        public byte[] Take()
        {
            if (ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                Dispose();
                throw InvalidSlot();
            }

            return Interlocked.Exchange(ref _value, null)
                ?? throw InvalidSlot();
        }

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _value, null);
            if (value is not null)
            {
                CryptographicOperations.ZeroMemory(value);
            }
        }
    }
}
