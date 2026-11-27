using DLSSManager.Core;

namespace DLSSManager.App;

public sealed class LocalizedText
{
    private readonly Func<string> render;

    private LocalizedText(Func<string> render) => this.render = render;

    internal static LocalizedText Message(string key, params object?[] arguments) =>
        new(() => LocalizationService.GetText(key, arguments));

    public string Text => render();
    public int Length => Text.Length;
    public override string ToString() => Text;

    public static implicit operator LocalizedText(string text) => new(() => text);

    public static LocalizedText operator +(LocalizedText left, LocalizedText right) =>
        new(() => left.Text + right.Text);
}
