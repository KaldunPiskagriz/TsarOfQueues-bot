using System.Text.Json;
using System.Text.Json.Serialization;
using Telegram.Bot.Types;

namespace QueueKingBot;

public static class LocalesTextHolder
{
    static Dictionary<string, Dictionary<LocaleKeys, string>> locales;
    static ApplicationContext db;
    
    static LocalesTextHolder()
    {
        db = new ApplicationContext();
        string jsonLocales = File.ReadAllText("locales.json");
        JsonSerializerOptions options = new JsonSerializerOptions() { Converters = { new JsonStringEnumConverter() } };
        locales = JsonSerializer.Deserialize<Dictionary<string, Dictionary<LocaleKeys, string>>>(jsonLocales, options) ?? new();
    }

    public static string GetText(ChatId chatId, LocaleKeys key)
    {
        QueueChatLocaleEntry? entry = db.QueueChatLocales.FirstOrDefault(item => item.ChatId == chatId.Identifier);
        if (entry != null) return locales[entry.LocaleName][key];
        entry = new QueueChatLocaleEntry { ChatId = chatId.Identifier.Value, LocaleName = "en" };
        db.QueueChatLocales.Add(entry);
        db.SaveChanges();
        return locales[entry.LocaleName][key];
    }

    public static string GetText(int queueId, LocaleKeys key)
    {
        return GetText(db.QueueDatas.First(item => item.QueueId == queueId).QueueChatId, key);
    }

    public static bool HasLocale(string localeName)
    {
        return locales.ContainsKey(localeName);
    }
}