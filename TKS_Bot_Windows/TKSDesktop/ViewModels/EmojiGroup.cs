using TKSDesktop.App;

namespace TKSDesktop.ViewModels;

public sealed record EmojiGroup(string Key, IReadOnlyList<string> Glyphs)
{
    public string Label => I18n.T(Key);

    public static IReadOnlyList<EmojiGroup> All { get; } =
    [
        new("emoji.smileys", ["😀", "😃", "😄", "😁", "😆", "😅", "😂", "🙂", "🙃", "😉", "😊", "😍", "🥰", "😘", "🤔", "😭"]),
        new("emoji.gestures", ["👍", "👎", "👏", "🙌", "👋", "🤝", "✌️", "🤞", "👌", "🙏", "💪", "🫶"]),
        new("emoji.hearts", ["❤️", "🧡", "💛", "💚", "💙", "💜", "🖤", "🤍", "💖", "💕", "💔", "💗"]),
        new("emoji.animals", ["🐼", "🐱", "🐶", "🐰", "🐻", "🦊", "🐸", "🐧", "🐥", "🦋", "🐬", "🐾"]),
        new("emoji.food", ["🍎", "🍓", "🍒", "🍉", "🍔", "🍕", "🍜", "🍣", "🍰", "🍪", "☕", "🧋"]),
        new("emoji.objects", ["⭐", "🌙", "☀️", "🌈", "⚡", "🎵", "🎸", "🎁", "🎉", "📚", "💡", "🌸"]),
    ];
}
