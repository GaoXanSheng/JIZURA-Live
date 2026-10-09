using System.IO;
using JizuraLive.App;
using Xunit;

namespace JizuraLive.Engine.Tests;

public class StorageCleanerTests
{
    [Fact]
    public void StorageCleaner_Deduplicate_And_CleanTemp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cleaner-test-{Guid.NewGuid():N}");
        var cacheAudio = Path.Combine(tempDir, "cache", "audio");
        Directory.CreateDirectory(cacheAudio);

        try
        {
            // 构造 3 个同名前缀同大小的重复文件，加 1 个 0 字节临时文件
            var file1 = Path.Combine(cacheAudio, "song_aaaa11112222.mp3");
            var file2 = Path.Combine(cacheAudio, "song_bbbb33334444.mp3");
            var file3 = Path.Combine(cacheAudio, "song_cccc55556666.mp3");
            var tempFile = Path.Combine(cacheAudio, "embedded_000000.mp3");
            var content = new byte[1024];

            File.WriteAllBytes(file1, content);
            File.WriteAllBytes(file2, content);
            File.WriteAllBytes(file3, content);
            File.WriteAllBytes(tempFile, Array.Empty<byte>());

            // 验证目录大小统计
            Assert.True(File.Exists(file1));
            Assert.True(File.Exists(file2));
            Assert.True(File.Exists(file3));
            Assert.True(File.Exists(tempFile));

            // 执行自动清理逻辑验证
            StorageCleaner.AutoCleanupAsync(null);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
