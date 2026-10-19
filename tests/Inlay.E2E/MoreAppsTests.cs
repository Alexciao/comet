using Inlay.E2E.Harness;

namespace Inlay.E2E;

/// <summary>Emoji and shorthands in Windows Terminal, VS Code (Electron) and Word, when installed.</summary>
[Collection(Desktop.Name)]
public sealed class MoreAppsTests : IDisposable
{
    private static readonly TimeSpan s_settle = TimeSpan.FromSeconds(5);

    private readonly InlayApp _inlay = new InlayApp().WithShorthands(("cmq", "comunque"));
    private ITextTarget? _target;

    public MoreAppsTests() => _inlay.Start();

    public void Dispose()
    {
        _target?.Dispose();
        _inlay.Dispose();
    }

    private void Exercise(ITextTarget target)
    {
        _target = target;
        target.Focus();
        // Starts with a digit so Word's AutoCorrect doesn't capitalize the first word.
        Keyboard.Type("2 ok :fire: cmq :thumbsu");
        _inlay.WaitForPopup();
        Keyboard.Press(Keyboard.VK_RETURN);
        Assert.Equal("2 ok 🔥 comunque 👍", Wait.For(target.ReadText, t => t == "2 ok 🔥 comunque 👍", s_settle, $"{target.Name} to read the result"));
    }

    [Fact]
    public void Windows_terminal() => Exercise(new TerminalTarget());

    [Fact]
    public void Vs_code()
    {
        if (VsCodeTarget.Executable is null)
            Assert.Skip("VS Code is not installed.");
        Exercise(new VsCodeTarget());
    }

    [Fact]
    public void Word()
    {
        if (WordTarget.Executable is null)
            Assert.Skip("Word is not installed.");
        if (WordTarget.AlreadyRunning)
            Assert.Skip("Word is open with the user's documents; the test only runs against a Word it starts itself.");
        Exercise(new WordTarget());
    }
}
