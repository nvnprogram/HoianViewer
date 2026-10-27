namespace PlayerViewer.Core
{
    /// <summary>The game a romfs belongs to, by its codename.</summary>
    public enum GameKind
    {
        Thunder, // Splatoon 3
        Spiral, // Splatoon Raiders
    }

    public static class GameNames
    {
        public static string Title(GameKind game) =>
            game == GameKind.Spiral ? "Splatoon Raiders" : "Splatoon 3";

        /// <summary>The menu label: codename and title.</summary>
        public static string Label(GameKind game) => $"{game} ({Title(game)})";
    }
}
