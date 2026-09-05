using System.Security.Cryptography;
using System.Text;
using Google.Apis.Util.Store;
using Newtonsoft.Json;

internal sealed class DpapiJsonDataStore : IDataStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public DpapiJsonDataStore(string path)
    {
        _path = path;
    }

    public Task StoreAsync<T>(string key, T value)
    {
        lock (_gate)
        {
            var map = ReadMap();
            map[key] = JsonConvert.SerializeObject(value);
            WriteMap(map);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        lock (_gate)
        {
            var map = ReadMap();
            if (map.Remove(key))
            {
                WriteMap(map);
            }
        }

        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        lock (_gate)
        {
            var map = ReadMap();
            if (!map.TryGetValue(key, out var json) || string.IsNullOrWhiteSpace(json))
            {
                return Task.FromResult(default(T)!);
            }

            return Task.FromResult(JsonConvert.DeserializeObject<T>(json)!);
        }
    }

    public Task ClearAsync()
    {
        lock (_gate)
        {
            GoogleOAuthUtil.TryDelete(_path);
        }

        return Task.CompletedTask;
    }

    private Dictionary<string, string> ReadMap()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var plain = GoogleOAuthUtil.UnprotectTokenBytes(File.ReadAllBytes(_path));
            var json = Encoding.UTF8.GetString(plain);
            return JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or CryptographicException or IOException
            or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void WriteMap(Dictionary<string, string> map)
    {
        var json = JsonConvert.SerializeObject(map);
        GoogleOAuthUtil.WriteAtomicProtected(_path, Encoding.UTF8.GetBytes(json));
    }
}
