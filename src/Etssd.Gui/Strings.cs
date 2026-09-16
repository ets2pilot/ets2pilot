using System.Windows;

namespace Etssd.Gui;

/// <summary>按键读取 Strings.xaml 的文案。缺键时 FindResource 抛出。</summary>
internal static class Strings
{
    public static string Get(string key) => (string)Application.Current.FindResource(key);

    public static string Format(string key, params object?[] args) => string.Format(Get(key), args);
}
