using System.IO;
using FluentAssertions;
using Whisper.Client.Models;
using Whisper.Client.Services;
using Xunit;

namespace Whisper.Tests.Client;

public class ProfileStoreTests
{
    private static ProfileDocument SampleDocument() => new()
    {
        ClientId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Profiles =
        [
            new ServerProfile
            {
                Name = "Friends",
                Host = "192.168.1.20",
                Port = 5000,
                DisplayName = "Tom",
                Password = "super-secret-password",
                RememberPassword = true,
            },
        ],
    };

    [Fact]
    public void Load_WhenNoFileExists_ReturnsAnEmptyDocumentWithAnIdentity()
    {
        using var directory = new TemporaryDirectory();
        var store = new ProfileStore(new ReversibleSecretProtector(), directory.Path);

        var document = store.Load();

        document.Profiles.Should().BeEmpty();
        document.ClientId.Should().NotBe(Guid.Empty);
        document.Audio.Should().NotBeNull();
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsProfilesAndIdentity()
    {
        using var directory = new TemporaryDirectory();
        var store = new ProfileStore(new ReversibleSecretProtector(), directory.Path);

        store.Save(SampleDocument());
        var loaded = store.Load();

        loaded.ClientId.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var profile = loaded.Profiles.Should().ContainSingle().Subject;
        profile.Name.Should().Be("Friends");
        profile.Host.Should().Be("192.168.1.20");
        profile.Port.Should().Be(5000);
        profile.DisplayName.Should().Be("Tom");
        profile.Password.Should().Be("super-secret-password");
        profile.RememberPassword.Should().BeTrue();
    }

    [Fact]
    public void Save_NeverWritesThePasswordInPlainText()
    {
        using var directory = new TemporaryDirectory();
        var store = new ProfileStore(new ReversibleSecretProtector(), directory.Path);

        store.Save(SampleDocument());

        var contents = File.ReadAllText(store.FilePath);
        contents.Should().NotContain("super-secret-password");
        contents.Should().Contain(ReversibleSecretProtector.Prefix);
    }

    [Fact]
    public void Save_WithoutRememberPassword_DoesNotPersistTheSecretAtAll()
    {
        using var directory = new TemporaryDirectory();
        var store = new ProfileStore(new ReversibleSecretProtector(), directory.Path);
        var document = SampleDocument();
        document.Profiles[0].RememberPassword = false;

        store.Save(document);

        File.ReadAllText(store.FilePath).Should().NotContain(ReversibleSecretProtector.Prefix);
        store.Load().Profiles[0].Password.Should().BeEmpty();
    }

    [Fact]
    public void Load_WithCorruptJson_ReturnsAUsableDocumentInsteadOfThrowing()
    {
        // A client that cannot start because its settings file is damaged is unfixable
        // from inside the app, so loading degrades instead of failing.
        using var directory = new TemporaryDirectory();
        var store = new ProfileStore(new ReversibleSecretProtector(), directory.Path);
        File.WriteAllText(store.FilePath, "{ this is not json at all ");

        var document = store.Load();

        document.Profiles.Should().BeEmpty();
        document.ClientId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Load_WithATruncatedFile_Recovers()
    {
        using var directory = new TemporaryDirectory();
        var store = new ProfileStore(new ReversibleSecretProtector(), directory.Path);
        store.Save(SampleDocument());

        var full = File.ReadAllText(store.FilePath);
        File.WriteAllText(store.FilePath, full[..(full.Length / 2)]);

        store.Load().Profiles.Should().BeEmpty();
    }

    [Fact]
    public void Load_WithJsonNullLiteral_ReturnsAnEmptyDocument()
    {
        using var directory = new TemporaryDirectory();
        var store = new ProfileStore(new ReversibleSecretProtector(), directory.Path);
        File.WriteAllText(store.FilePath, "null");

        var document = store.Load();

        document.Profiles.Should().BeEmpty();
        document.ClientId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Load_WhenTheSecretCannotBeDecrypted_KeepsTheProfileAndClearsThePassword()
    {
        using var directory = new TemporaryDirectory();
        new ProfileStore(new ReversibleSecretProtector(), directory.Path).Save(SampleDocument());

        var onAnotherMachine = new ProfileStore(new FailingSecretProtector(), directory.Path).Load();

        var profile = onAnotherMachine.Profiles.Should().ContainSingle().Subject;
        profile.Host.Should().Be("192.168.1.20");
        profile.Password.Should().BeEmpty();
        profile.ProtectedPassword.Should().BeNull();
    }

    [Fact]
    public void Load_WithAnEmptyClientId_AssignsANewOne()
    {
        using var directory = new TemporaryDirectory();
        var store = new ProfileStore(new ReversibleSecretProtector(), directory.Path);
        var document = SampleDocument();
        document.ClientId = Guid.Empty;
        store.Save(document);

        store.Load().ClientId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Save_CreatesTheDirectoryWhenItIsMissing()
    {
        using var directory = new TemporaryDirectory();
        var nested = Path.Combine(directory.Path, "does", "not", "exist");
        var store = new ProfileStore(new ReversibleSecretProtector(), nested);

        store.Save(SampleDocument());

        File.Exists(store.FilePath).Should().BeTrue();
    }

    [Fact]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        using var directory = new TemporaryDirectory();
        var store = new ProfileStore(new ReversibleSecretProtector(), directory.Path);

        store.Save(SampleDocument());

        File.Exists(store.FilePath + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void Save_PersistsAudioSettings()
    {
        using var directory = new TemporaryDirectory();
        var store = new ProfileStore(new ReversibleSecretProtector(), directory.Path);
        var document = SampleDocument();
        document.Audio.UsePushToTalk = true;
        document.Audio.JitterBufferFrames = 5;
        document.Audio.VoiceActivityThresholdDb = -38f;

        store.Save(document);
        var loaded = store.Load();

        loaded.Audio.UsePushToTalk.Should().BeTrue();
        loaded.Audio.JitterBufferFrames.Should().Be(5);
        loaded.Audio.VoiceActivityThresholdDb.Should().Be(-38f);
    }
}
