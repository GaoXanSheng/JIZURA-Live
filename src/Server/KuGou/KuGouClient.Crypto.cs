using System.IO;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Server;

public sealed partial class KuGouClient
{
    private const string WebSalt = "NVPh5oo715z5DIWAeQlhMDsWXXQV4hwt";
    private const string AndroidSalt = "OIlwieks28dk2k092lksi2UIkp";
    private const string KeySalt = "57ae12eb6890223e355ccfcb74edf70d";
    private const string AppId = "1005";
    private const string ClientVer = "20489";
    private const string SrcAppId = "2919";
    private const string AndroidUa = "Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi";

    private const string PubKeyPem = """
                                     -----BEGIN PUBLIC KEY-----
                                     MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDIAG7QOELSYoIJvTFJhMpe1s/gbjDJX51HBNnEl5HXqTW6lQ7LC8jr9fWZTwusknp+sVGzwd40MwP6U5yDE27M/X1+UR4tvOGOqp94TJtQ1EPnWGWXngpeIW5GxoQGao1rmYWAu6oi1z9XkChrsUdC6DJE5E221wf/4WLFxwAtRQIDAQAB
                                     -----END PUBLIC KEY-----
                                     """;

    private static readonly (string Key, string Value)[] GatewayHeaders =
        [("kg-rc", "1"), ("kg-thash", "5d816a0"), ("kg-rec", "1"), ("kg-rf", "B9EDA08A64250DEFFBCADDEE00F8F25F")];

    // 酷狗登录状态（含 cookie）与 settings.json 同放 %LOCALAPPDATA%\JizuraLive\config
    private static readonly string StateFile = Path.Combine(AppDirs.ConfigDir, "kg_login.json");
    private static readonly SemaphoreSlim _stateLock = new(1, 1);

    private static JObject KgStateInternal()
    {
        try
        {
            if (File.Exists(StateFile))
                return JObject.Parse(File.ReadAllText(StateFile));
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to read KuGou state file: " + ex);
        }

        return [];
    }

    private static JObject KgState()
    {
        _stateLock.Wait();
        try
        {
            return KgStateInternal();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private static JObject KgSave(JObject patch)
    {
        _stateLock.Wait();
        try
        {
            var s = KgStateInternal();
            foreach (var p in patch.Properties()) s[p.Name] = p.Value;
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, s.ToString(Formatting.Indented));
            return s;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private static string S(JObject o, string key) => o[key]?.ToString() ?? "";


    private static JObject EnsureIdentity()
    {
        var st = KgState();
        var patch = new JObject();
        string uuid = S(st, "uuid");
        if (uuid.Length == 0)
        {
            uuid = Guid.NewGuid().ToString();
            patch["uuid"] = uuid;
            st["uuid"] = uuid;
        }

        if (S(st, "mid").Length == 0)
        {
            BigInteger midVal = 0;
            foreach (char c in Md5(uuid)) midVal = midVal * 16 + (c <= '9' ? c - '0' : c - 'a' + 10);
            string mid = midVal.ToString();
            patch["mid"] = mid;
            st["mid"] = mid;
        }

        if (S(st, "dev").Length == 0)
        {
            string dev = Md5(RandomKg(16));
            patch["dev"] = dev;
            st["dev"] = dev;
        }

        return patch.Count > 0 ? KgSave(patch) : st;
    }


    private static string Md5(string s) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string RandomKg(int n)
    {
        const string cs = "1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++) sb.Append(cs[Random.Shared.Next(cs.Length)]);
        return sb.ToString();
    }


    private static (string Url, string ClientTime) SignedUrl(
        string baseUrl, string urlPath, Dictionary<string, string> parameters, bool android, string data = "")
    {
        string now = ((int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000)).ToString();
        var q = new Dictionary<string, string>
        {
            ["dfid"] = "-", ["mid"] = "-", ["uuid"] = "-",
            ["appid"] = AppId, ["clientver"] = ClientVer, ["clienttime"] = now,
        };
        foreach (var (k, v) in parameters) q[k] = v;

        string salt = android ? AndroidSalt : WebSalt;
        string plain = string.Concat(q.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => $"{k}={q[k]}"));
        q["signature"] = Md5($"{salt}{plain}{data}{salt}");
        string qs = string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return ($"{baseUrl}{urlPath}?{qs}", now);
    }

    private static void AddGatewayHeaders(HttpRequestMessage req, string? dfid = null, string? clientTime = null,
        string? mid = null)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", AndroidUa);
        if (dfid != null) req.Headers.TryAddWithoutValidation("dfid", dfid);
        if (clientTime != null) req.Headers.TryAddWithoutValidation("clienttime", clientTime);
        if (mid != null) req.Headers.TryAddWithoutValidation("mid", mid);
        foreach (var (k, v) in GatewayHeaders) req.Headers.TryAddWithoutValidation(k, v);
    }


    private static string AesCbc(byte[] key, byte[] iv, string plainText)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor();
        return Convert.ToBase64String(enc.TransformFinalBlock(Encoding.UTF8.GetBytes(plainText), 0, plainText.Length));
    }

    private static string AesCbcDec(byte[] key, byte[] iv, byte[] cipher)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var dec = aes.CreateDecryptor();
        return Encoding.UTF8.GetString(dec.TransformFinalBlock(cipher, 0, cipher.Length));
    }


    private async Task<string> RegisterDevAsync(string token, long userid)
    {
        var st = EnsureIdentity();
        string key6 = RandomKg(6).ToLowerInvariant();
        string kmd5 = Md5(key6);
        byte[] aesKey = Encoding.ASCII.GetBytes(kmd5[..16]);
        byte[] aesIv = Encoding.ASCII.GetBytes(kmd5[16..32]);

        string deviceJson = """
                            {"availableRamSize":4983533568,"availableRomSize":48114719,"availableSDSize":48114717,"basebandVer":"","batteryLevel":100,"batteryStatus":3,"brand":"Redmi","buildSerial":"unknown","device":"marble","imei":"UUID","imsi":"","manufacturer":"Xiaomi","uuid":"UUID","accelerometer":false,"accelerometerValue":"","gravity":false,"gravityValue":"","gyroscope":false,"gyroscopeValue":"","light":false,"lightValue":"","magnetic":false,"magneticValue":"","orientation":false,"orientationValue":"","pressure":false,"pressureValue":"","step_counter":false,"step_counterValue":"","temperature":false,"temperatureValue":""}
                            """.Replace("UUID", S(st, "uuid")).Replace("\r", "").Replace("\n", "");
        string aesStr = AesCbc(aesKey, aesIv, deviceJson);

        string portrait;
        using (var rsa = RSA.Create())
        {
            rsa.ImportFromPem(PubKeyPem);
            string inner = $$"""{"aes":"{{key6}}","uid":{{userid}},"token":"{{token}}"}""";
            portrait = Convert.ToHexString(rsa.Encrypt(Encoding.UTF8.GetBytes(inner), RSAEncryptionPadding.Pkcs1))
                .ToLowerInvariant();
        }

        var (url, clientTime) = SignedUrl("https://userservice.kugou.com", "/risk/v2/r_register_dev",
            new Dictionary<string, string>
            {
                ["dfid"] = "-", ["mid"] = S(st, "mid"), ["part"] = "1", ["platid"] = "1", ["p"] = portrait,
            }, android: true, data: aesStr);

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(aesStr) };
        AddGatewayHeaders(req, dfid: "-", clientTime: clientTime, mid: S(st, "mid"));

        using var cts = new CancellationTokenSource(20000);
        using var resp = await Http.SendAsync(req, cts.Token);
        byte[] raw = await resp.Content.ReadAsByteArrayAsync(cts.Token);
        var body = JObject.Parse(AesCbcDec(aesKey, aesIv, raw));
        string? dfid = body["data"]?["dfid"]?.ToString();
        if (string.IsNullOrEmpty(dfid))
        {
            string rawJson = body.ToString(Formatting.None);
            throw new InvalidOperationException("register_dev 失败：" + rawJson[..Math.Min(200, rawJson.Length)]);
        }

        KgSave(new JObject { ["dfid"] = dfid });
        return dfid!;
    }
}