using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Server;

// 网易云加密与登录态。
// 加密方案与常量经 2026-09 实测验证（对齐 NeteaseCloudMusicApi v4.2.0 的 util/crypto.js）：
//   weapi：AES-CBC 两重（presetKey → 随机 16 字符 key），RSA 无填充加密反转后的随机 key
//   eapi ：AES-128-ECB，明文 = url-36cd479b6b5-text-36cd479b6b5-md5("nobody"+url+"use"+text+"md5forencrypt")，响应同样用 eapi key 解密
public sealed partial class NeteaseClient
{
    private const string WeapiPresetKey = "0CoJUm6Qyw8W8jud";
    private const string WeapiIv = "0102030405060708";
    private const string EapiKey = "e82ckenh8dichen8";
    private const string EapiSep = "36cd479b6b5";
    private const string AppVer = "8.9.75";
    private const string PcUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/122.0.0.0 Safari/537.36";
    private const string PubKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDgtQn2JZ34ZC28NWYpAUd98iZ37BUrX/aKzmFbt7clFSs6sXqHauqKWqdtLkF2KexO40H1YTX8z2lSgBBOAxLsvaklV8k4cBFK9snQXE9/DDaFt6Rr7iVZMldczhC0JNgTz+SHXT6CBHuX3e9SdB1Ua44oncaTWz7OBGLbCiK45wIDAQAB
        -----END PUBLIC KEY-----
        """;

    // 登录状态（MUSIC_U cookie 等）与 settings.json 同放应用的 config 文件夹（同 kg_login.json 惯例）
    private static readonly string StateFile = Path.Combine(
        AppContext.BaseDirectory, "config", "ne_login.json");

    private static JObject NeState()
    {
        try
        {
            if (File.Exists(StateFile))
                return JObject.Parse(File.ReadAllText(StateFile));
        }
        catch { }
        return [];
    }

    private static JObject NeSave(JObject patch)
    {
        var s = NeState();
        foreach (var p in patch.Properties()) s[p.Name] = p.Value;
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, s.ToString(Formatting.Indented));
        return s;
    }

    private static string S(JObject o, string key) => o[key]?.ToString() ?? "";

    private static string Md5Hex(string s) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /* ---------- weapi ---------- */

    private static byte[] AesCbc(byte[] key, string plain)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = Encoding.ASCII.GetBytes(WeapiIv);
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor();
        byte[] b = Encoding.UTF8.GetBytes(plain);
        return enc.TransformFinalBlock(b, 0, b.Length);
    }

    // 大端字节块 → 正 BigInteger（前补 0x00 防止符号位）
    private static BigInteger ToBig(byte[] bigEndian)
    {
        byte[] le = new byte[bigEndian.Length + 1];
        for (int i = 0; i < bigEndian.Length; i++) le[i] = bigEndian[bigEndian.Length - 1 - i];
        le[bigEndian.Length] = 0;
        return new BigInteger(le);
    }

    // RSA 无填充：数据左补零到 128 字节当大端整数，modpow(e, n)，结果 256 位十六进制
    private static string RsaNoPadHex(BigInteger n, BigInteger e, byte[] data)
    {
        byte[] block = new byte[128];
        Array.Copy(data, 0, block, 128 - data.Length, data.Length);
        byte[] be = BigInteger.ModPow(ToBig(block), e, n).ToByteArray();
        Array.Reverse(be);
        int start = 0;
        while (start < be.Length - 1 && be[start] == 0) start++;
        return Convert.ToHexString(be, start, be.Length - start).ToLowerInvariant().PadLeft(256, '0');
    }

    private static (string Params, string EncSecKey) WeapiEncrypt(string text)
    {
        const string cs = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        byte[] secKey = new byte[16];
        for (int i = 0; i < secKey.Length; i++) secKey[i] = (byte)cs[Random.Shared.Next(cs.Length)];
        string step1 = Convert.ToBase64String(AesCbc(Encoding.ASCII.GetBytes(WeapiPresetKey), text));
        string p = Convert.ToBase64String(AesCbc(secKey, step1));
        Array.Reverse(secKey);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PubKeyPem);
        var pk = rsa.ExportParameters(false);
        return (p, RsaNoPadHex(ToBig(pk.Modulus!), ToBig(pk.Exponent!), secKey));
    }

    /* ---------- eapi ---------- */

    private static string EapiParams(string apiUrl, string text)
    {
        string digest = Md5Hex($"nobody{apiUrl}use{text}md5forencrypt");
        string plain = $"{apiUrl}-{EapiSep}-{text}-{EapiSep}-{digest}";
        byte[] key = Encoding.ASCII.GetBytes(EapiKey);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var enc = aes.CreateEncryptor();
        byte[] b = Encoding.UTF8.GetBytes(plain);
        return Convert.ToHexString(enc.TransformFinalBlock(b, 0, b.Length));
    }

    private static string EapiDecrypt(byte[] cipher)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(EapiKey);
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var dec = aes.CreateDecryptor();
        return Encoding.UTF8.GetString(dec.TransformFinalBlock(cipher, 0, cipher.Length));
    }

    public static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
}
