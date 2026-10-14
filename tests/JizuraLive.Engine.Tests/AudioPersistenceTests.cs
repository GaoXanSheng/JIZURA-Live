using System.IO;
using System.Text;
using JizuraLive.App;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public sealed class AudioPersistenceTests : IClassFixture<AutosaveGuard>
{
    [Fact]
    public void ProjectIO_SerializesAndRestores_AudioFields()
    {
        var p = Project.NewDefault();
        p.AudioName = "test_song.mp3";
        p.AudioPath = @"C:\Music\test_song.mp3";

        var json = ProjectIO.SerializeProject(p);
        var raw = JObject.Parse(json);

        Assert.Equal("test_song.mp3", (string?)raw["audioName"]);
        Assert.Equal(@"C:\Music\test_song.mp3", (string?)raw["audioPath"]);

        var restored = ProjectIO.MergeProject(raw);
        Assert.Equal("test_song.mp3", restored.AudioName);
        Assert.Equal(@"C:\Music\test_song.mp3", restored.AudioPath);
    }

    [Fact]
    public void EditorState_ResolveAudioPath_FindsExistingOrCachedAudio()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"direct_{Guid.NewGuid():N}.mp3");
        try
        {
            File.WriteAllText(tempFile, "dummy");

            // 1. 直读存在的文件
            var resolved = EditorState.ResolveAudioPath(tempFile, "direct.mp3");
            Assert.Equal(tempFile, resolved);

            // 2. 原路径不存在，但缓存中存在 autosave_song
            Directory.CreateDirectory(AppDirs.AudioCacheDir);
            var autosaveSong = Path.Combine(AppDirs.AudioCacheDir, "autosave_song.mp3");
            File.WriteAllText(autosaveSong, "cached_content");

            var nonExistentPath = @"Z:\NonExistent\audio.mp3";
            var fromCache = EditorState.ResolveAudioPath(nonExistentPath, "audio.mp3");
            Assert.NotNull(fromCache);
            Assert.True(File.Exists(fromCache));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
