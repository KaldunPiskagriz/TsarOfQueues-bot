using Newtonsoft.Json;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;

#region Workflow
// replace YOUR_BOT_TOKEN below
var token = "";

if(token == "")
{
    token = File.ReadAllText("bot_token.txt");
}

using var cts = new CancellationTokenSource();
var bot = new TelegramBotClient(token, cancellationToken: cts.Token);

var me = await bot.GetMe();
await bot.DeleteWebhook();          // you may comment this line if you find it unnecessary
await bot.DropPendingUpdates();     // you may comment this line if you find it unnecessary

Dictionary<string, Dictionary<string, string>> locales = new();
string jsonLocales = File.ReadAllText("locales.json");
locales = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(jsonLocales) ?? new();

HashSet<int> waitQueueDeletion = new();
using (ApplicationContext db = new ApplicationContext())
{
    DateTime nowTime = DateTime.Now;
    foreach (QueueDataEntry item in db.QueueDatas)
    {
        _ = DelayedDeleteQueue(item.ExpireDate.Subtract(nowTime), item.QueueId);
    }
}

bot.OnError += OnError;
bot.OnMessage += OnMessage;
bot.OnUpdate += OnUpdate;

Console.WriteLine($"@{me.Username} is running... Press Escape to terminate");
while (Console.ReadKey(true).Key != ConsoleKey.Escape) ;
cts.Cancel();
Console.WriteLine("stopped");
Console.ReadLine();
#endregion

#region Functions
int GetNextQueueId()
{
    ApplicationContext db = new ApplicationContext();
    if (!db.QueueDatas.Any()) { return 0; }
    return db.QueueDatas.OrderByDescending(item => item.QueueId).First().QueueId + 1;
}

string GetBeautifulQueueStringOutput(int queueId)
{
    string output = $"{FindTextWithQueue(queueId, "QueueOuput_Queue")}: <b>{GetQueueName(queueId)}</b>\n";
    QueueDataEntry entry = FindQueue(queueId);
    if (entry.ExpireDate.Subtract(DateTime.Now).ToString().StartsWith('-'))
    {
        output += $"{FindTextWithQueue(queueId, "QueueOuput_Expired")}\n\n";
    }
    else
    {
        DateTime expireTime = entry.ExpireDate;
        output += $"{FindTextWithQueue(queueId, "QueueOuput_Expires")}: {string.Format("{0:d2}", expireTime.Day)}.{string.Format("{0:d2}", expireTime.Month)}.{string.Format("{0:d4}", expireTime.Year)} {string.Format("{0:d2}", expireTime.Hour)}:{string.Format("{0:d2}", expireTime.Minute)}\n\n";
    }
    ApplicationContext db = new ApplicationContext();
    List<QueueUserEntry> entries = db.Queues.Where(item => item.QueueId == queueId).ToList();
    if(entries.Count == 0)
    {
        output += $"<i>{FindTextWithQueue(queueId, "QueueOuput_QueueIsEmpty")}</i>";
        return output;
    }
    for(int i = 0; i < entries.Count; i++)
    {
        output += $"{i + 1}. {entries[i].UserName}\n";
    }
    return output;
}

async Task UpdateQueueMessage(ChatId chatId, int messageId, int queueId)
{
    await bot.EditMessageText(chatId,
                messageId,
                GetBeautifulQueueStringOutput(queueId),
                parseMode: ParseMode.Html,
                replyMarkup: FindQueue(queueId).ExpireDate.Subtract(DateTime.Now).ToString().StartsWith('-') ? null : 
                new InlineKeyboardButton[][]
                {
                    [(FindTextWithQueue(queueId, "QueueOuput_EnterQueue"), $"QueueEnter:{queueId.ToString()}")],
                    [(FindTextWithQueue(queueId, "QueueOuput_LeaveQueue"), $"QueueLeave:{queueId.ToString()}")],
                    [(FindTextWithQueue(queueId, "QueueOuput_LetAhead"), $"QueueLet:{queueId.ToString()}")]
                });
}

async Task<Message> SendQueueMessage(ChatId chatId, int queueId)
{
    Message queueMsg = await bot.SendMessage(chatId, $"{FindTextWithQueue(queueId, "QueueOuput_Queue")}: <b>{GetQueueName(queueId)}</b>\n\n");
    await UpdateQueueMessage(chatId, queueMsg.Id, queueId);
    return queueMsg;
}

string GetQueueName(int queueId)
{
    return FindQueue(queueId).Name;
}

void SwapProperties<T>(T source, T destination)
{
    var properties = typeof(T).GetProperties();
    foreach (var property in properties)
    {
        if (property.CanRead && property.CanWrite && property.ToString() != "Int32 Id")
        {
            var tempValue = property.GetValue(destination, null);
            property.SetValue(destination, property.GetValue(source, null), null);
            property.SetValue(source, tempValue, null);
        }
    }
}

string GetChatLocale(long chatId)
{
    ApplicationContext db = new ApplicationContext();
    QueueChatLocaleEntry? queueChatEntry = db.QueueChatLocales.FirstOrDefault(item => item.ChatId == chatId);
    if(queueChatEntry == null)
    {
        queueChatEntry = new QueueChatLocaleEntry();
        queueChatEntry.ChatId = chatId;
        queueChatEntry.LocaleName = "en";
        db.Add(queueChatEntry);
        db.SaveChanges();
        return "en";
    }
    else
    {
        return queueChatEntry.LocaleName;
    }
}

string FindTextWithChat(long chatId, string textStringKey)
{
    return locales[GetChatLocale(chatId)][textStringKey];
}
string FindTextWithQueue(int queueId, string textStringKey)
{
    return locales[GetChatLocale(FindQueue(queueId).QueueChatId)][textStringKey];
}

QueueDataEntry FindQueue(int queueId)
{
    ApplicationContext db = new ApplicationContext();
    QueueDataEntry? entry = db.QueueDatas.FirstOrDefault(item => item.QueueId == queueId);
    if(entry == null)
    {
        Console.WriteLine($"Cannot find queue: queue with id {queueId} doesn't exist");
        return new QueueDataEntry();
    }
    return entry;
}

async Task DelayedDeleteQueue(TimeSpan delay, int queueId)
{
    waitQueueDeletion.Add(queueId);
    Console.WriteLine($"Waiting {delay} to delete queue {queueId}");
    if(!delay.ToString().StartsWith('-')) await Task.Delay(delay);
    Console.WriteLine($"Deleting queue {queueId}");
    ApplicationContext db = new ApplicationContext();
    QueueDataEntry entry = FindQueue(queueId);
    QueueUserEntry[] users = db.Queues.Where(item => item.QueueId == queueId).ToArray();
    try
    {
        await UpdateQueueMessage(entry.QueueChatId, entry.QueueMessageId, entry.QueueId);
    }
    catch (ApiRequestException) { }
    db.QueueDatas.Remove(entry);
    db.RemoveRange(users);
    db.SaveChanges();
    waitQueueDeletion.Remove(queueId);
}
#endregion

#region Bot commands
async Task HandleQueueButton(CallbackQuery callbackQuery)
{
    Console.WriteLine($"Received callback querry: {callbackQuery.Data}");
    if(callbackQuery.Data == null || callbackQuery.Message == null) { return; }
    int queueId = int.Parse(callbackQuery.Data[(callbackQuery.Data.IndexOf(':') + 1)..]);
    ApplicationContext db = new ApplicationContext();
    if (db.QueueDatas.FirstOrDefault(item => item.QueueId == queueId) == null)
    {
        await bot.AnswerCallbackQuery(callbackQuery.Id, $"{FindTextWithChat(callbackQuery.Message.Chat.Id, "Callback_QueueIsDeleted")}");
        return;
    }
    if (callbackQuery.Data.StartsWith("QueueEnter:"))
    {
        QueueUserEntry? checkUser = db.Queues.FirstOrDefault(item => item.UserId == callbackQuery.From.Id && item.QueueId == queueId);
        if (checkUser != null)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, $"{FindTextWithChat(callbackQuery.Message.Chat.Id, "Callback_YouAreInQueue")} {GetQueueName(queueId)}");
        }
        else
        {
            QueueUserEntry entry = new QueueUserEntry()
            {
                UserId = callbackQuery.From.Id,
                UserName = callbackQuery.From.FirstName + " " + callbackQuery.From.LastName,
                QueueId = queueId
            };
            db.Add(entry);
            db.SaveChanges();
            await UpdateQueueMessage(callbackQuery.Message.Chat.Id, callbackQuery.Message.Id, queueId);
            await bot.AnswerCallbackQuery(callbackQuery.Id, $"{FindTextWithChat(callbackQuery.Message.Chat.Id, "Callback_QueueEntered")} {GetQueueName(queueId)}");
            Console.WriteLine($"user {callbackQuery.From.Id} added to queue {queueId}");
        }
    }
    else if (callbackQuery.Data.StartsWith("QueueLeave:"))
    {
        QueueUserEntry? entry = db.Queues.FirstOrDefault(item => item.UserId == callbackQuery.From.Id && item.QueueId == queueId);
        if (entry == null)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, $"{FindTextWithChat(callbackQuery.Message.Chat.Id, "Callback_NotInQueue")} {GetQueueName(queueId)}");
        }
        else
        {
            db.Remove(entry);
            db.SaveChanges();
            await UpdateQueueMessage(callbackQuery.Message.Chat.Id, callbackQuery.Message.Id, queueId);
            await bot.AnswerCallbackQuery(callbackQuery.Id, $"{FindTextWithChat(callbackQuery.Message.Chat.Id, "Callback_QueueLeft")} {GetQueueName(queueId)}");
            Console.WriteLine($"user {callbackQuery.From.Id} removed from queue {queueId}");
        }
    }
    else if (callbackQuery.Data.StartsWith("QueueLet:"))
    {
        QueueUserEntry? initialUser = null, nextUser = null;
        foreach (QueueUserEntry item in db.Queues)
        {
            if (initialUser == null)
            {
                if (item.UserId == callbackQuery.From.Id && item.QueueId == queueId)
                {
                    initialUser = item;
                }
            }
            else
            {
                if (item.QueueId == queueId)
                {
                    nextUser = item;
                    break;
                }
            }
        }
        if (initialUser == null)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, $"{FindTextWithChat(callbackQuery.Message.Chat.Id, "Callback_NotInQueue")} {GetQueueName(queueId)}");
            return;
        }
        if (nextUser == null)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, $"{FindTextWithChat(callbackQuery.Message.Chat.Id, "Callback_NobodyToLet")} {GetQueueName(queueId)}");
            return;
        }
        Console.WriteLine("trying to swap users");
        SwapProperties<QueueUserEntry>(initialUser, nextUser);
        db.Update(initialUser);
        db.Update(nextUser);
        db.SaveChanges();
        await UpdateQueueMessage(callbackQuery.Message.Chat.Id, callbackQuery.Message.Id, queueId);
        Console.WriteLine($"swapped data of users {initialUser.UserId} and {nextUser.UserId}");
    }
}
async Task CreateQueueCommand(string args, Message msg)
{
    ApplicationContext db = new ApplicationContext();
    Console.WriteLine("Executing command \"/createqueue\"");
    string? queueName = null; char? timeType = null; string? queueTime = null;
    int typeIndex = args.LastIndexOf(" -time:");
    DateTime now = DateTime.Now;
    if (typeIndex == -1)
    {
        typeIndex = args.LastIndexOf(" -date:");
        if(typeIndex > -1)
        {
            timeType = 'd';
            queueTime = args[(typeIndex + 7)..];
        }
    }
    else
    {
        timeType = 't';
        queueTime = args[(typeIndex + 7)..];
    }
    queueName = typeIndex == -1 ? args : args[..typeIndex];
    if (queueName == null)
    {
        await bot.SendMessage(msg.Chat.Id, $"{FindTextWithChat(msg.Chat.Id, "Command_EmpltyQueueName")}");
        return;
    }
    DateTime expireTime = DateTime.MinValue;
    TimeSpan expireSpan = TimeSpan.Zero;
    if(queueTime != null)
    {
        if (timeType == 'd')
        {
            if(!DateTime.TryParse(queueTime, out expireTime))
            {
                await bot.SendMessage(msg.Chat.Id, $"{FindTextWithChat(msg.Chat.Id, "Command_InvalidDateTime")}");
                return;
            }
            if (expireTime.Subtract(now).ToString().StartsWith('-'))
            {
                await bot.SendMessage(msg.Chat.Id, $"{FindTextWithChat(msg.Chat.Id, "Command_NegativeWaitTime")}");
                return;
            }
            if(expireTime.Hour == 0 && expireTime.Minute == 0 && expireTime.Second == 0)
            {
                expireTime.AddHours(now.Hour);
                expireTime.AddMinutes(now.Minute);
                expireTime.AddSeconds(now.Second);
            }
        }
        if (timeType == 't')
        {
            if (!TimeSpan.TryParse(queueTime, out expireSpan))
            {
                await bot.SendMessage(msg.Chat.Id, $"{FindTextWithChat(msg.Chat.Id, "Command_InvalidTimeSpan")}");
                return;
            }
            else
            {
                expireTime = now.Add(expireSpan);
            }
        }
    }
    int queueId = GetNextQueueId();
    QueueDataEntry entry = new QueueDataEntry()
    {
        QueueId = queueId,
        Name = queueName,
        ExpireDate = expireTime == DateTime.MinValue ? now.AddDays(7) : expireTime,
        QueueChatId = msg.Chat.Id
    };
    db.Add(entry);
    db.SaveChanges();
    Message queueMsg = await SendQueueMessage(msg.Chat.Id, queueId);
    entry.QueueMessageId = queueMsg.Id;
    db.Update(entry);
    db.SaveChanges();
    Console.WriteLine($"created queue \"{queueName}\":{queueId}, expires: {entry.ExpireDate}");
    _ = DelayedDeleteQueue(entry.ExpireDate.Subtract(now), queueId);
}

async Task ShowAllQueuesCommand(Message msg)
{
    Console.WriteLine("Executing /showallqueues command");
    ApplicationContext db = new ApplicationContext();
    if (!db.QueueDatas.Any(item => item.QueueChatId == msg.Chat.Id)) { await bot.SendMessage(msg.Chat.Id, FindTextWithChat(msg.Chat.Id, "Command_NoQueuesInChat")); return; }
    foreach (var queue in db.QueueDatas)
    {
        if (queue.QueueChatId != msg.Chat.Id) continue;
        try
        {
            ReplyParameters parameters = new ReplyParameters();
            parameters.ChatId = queue.QueueChatId;
            parameters.MessageId = queue.QueueMessageId;
            await bot.SendMessage(msg.Chat.Id, $"{FindTextWithChat(queue.QueueChatId, "Command_HereIsQueue")} {GetQueueName(queue.QueueId)}", replyParameters: parameters);
        }
        catch (ApiRequestException e)
        {
            Console.WriteLine(e.Message);
            if (e.ErrorCode == 400 && (e.Message.Contains("message to be replied not found")))
            {
                Message newQueueMessage = await SendQueueMessage(msg.Chat.Id, queue.QueueId);
                QueueDataEntry entryToUpdate = FindQueue(queue.QueueId);
                entryToUpdate.QueueMessageId = newQueueMessage.Id;
                db.Update(entryToUpdate);
                db.SaveChanges();
            }
        }
    }
}

async Task ShowMyQueuesCommand(Message msg)
{
    if (msg.From == null)
    {
        Console.WriteLine("Received /showmyqueues from null o_O");
        return;
    }
    Console.WriteLine("Executing /showmyqueues command");
    ApplicationContext db = new ApplicationContext();
    if (msg.Chat.Type != ChatType.Private) { await bot.SendMessage(msg.Chat.Id, FindTextWithChat(msg.Chat.Id, "Command_OnlyInPerson")); return; }
    List<int> queues = db.Queues.Where(item => item.UserId == msg.From.Id).Select(item => item.QueueId).ToList();
    if (!queues.Any()) { await bot.SendMessage(msg.Chat.Id, FindTextWithChat(msg.Chat.Id, "Command_YouAreNotInAnyQueue")); return; }
    foreach (int id in queues)
    {
        QueueDataEntry? myQueue = db.QueueDatas.FirstOrDefault(item => item.QueueId == id);
        if (myQueue == null) { Console.WriteLine($"There is no queue with id {id} in database"); continue; }
        string link = $"$\"https://t.me/c/{myQueue.QueueChatId.ToString().Substring(4)}/{myQueue.QueueMessageId}\"";
        string preparedQueueLink = $"{FindTextWithChat(msg.Chat.Id, "QueueOuput_Queue")} {myQueue.Name} {link}";
        await bot.SendMessage(msg.Chat.Id, preparedQueueLink);
        await bot.ForwardMessage(msg.Chat.Id, myQueue.QueueChatId, myQueue.QueueMessageId);
    }
}

async Task SetLocaleCommand(string args, Message msg)
{
    Console.WriteLine("Executing /setlocale command");
    if (!locales.ContainsKey(args))
    {
        await bot.SendMessage(msg.Chat.Id, $"{FindTextWithChat(msg.Chat.Id, "Command_LocaleNotFound")} {args}");
        return;
    }
    GetChatLocale(msg.Chat.Id);
    ApplicationContext db = new ApplicationContext();
    QueueChatLocaleEntry localeEntry = db.QueueChatLocales.First(item => item.ChatId == msg.Chat.Id);
    localeEntry.LocaleName = args;
    db.Update(localeEntry);
    db.SaveChanges();
    await bot.SendMessage(msg.Chat.Id, $"{FindTextWithChat(msg.Chat.Id, "Command_LocaleSet")} {args}");
}

async Task UsageCommand(Message msg)
{
    Console.WriteLine("Executing /usage command");
    string usageMessage = $"{FindTextWithChat(msg.Chat.Id, "Usage_Title")}" +
                $"\n\n/createqueue <{FindTextWithChat(msg.Chat.Id, "Usage_Name")}> - {FindTextWithChat(msg.Chat.Id, "Usage_CreateQueueUsage")}" +
                $"\n\n/showallqueues - {FindTextWithChat(msg.Chat.Id, "Usage_ShowAllQueues")}" +
                $"\n\n/showmyqueues - {FindTextWithChat(msg.Chat.Id, "Usage_ShowMyQueues")}" +
                $"\n\n/usage - {FindTextWithChat(msg.Chat.Id, "Usage_Usage")}" +
                $"\n\n/setlocale <{FindTextWithChat(msg.Chat.Id, "Usage_Name")}> - {FindTextWithChat(msg.Chat.Id, "Usage_SetLocale")}";
    await bot.SendMessage(msg.Chat.Id, usageMessage);
}
#endregion

#region Bot tasks
async Task OnError(Exception exception, HandleErrorSource source)
{
    Console.WriteLine(exception);
    await Task.Delay(2000, cts.Token); // delay for 2 seconds before eventually trying again
}

async Task OnMessage(Message msg, UpdateType type)
{
    if (msg.Text is not { } text)
        Console.WriteLine($"Received a message of type {msg.Type}");
    else if (text.StartsWith('/'))
    {
        Console.WriteLine($"Received a message: {msg.Text}");
        var space = text.IndexOf(' ');
        if (space < 0) space = text.Length;
        var command = text[..space].ToLower();
        string args = text[space..].TrimStart();
        Console.WriteLine($"Recognized command: \"{command}\"; Args:\"{args}\"");
        await OnCommand(command, args, msg);
    }
}

async Task OnCommand(string command, string args, Message msg)
{
    switch (command)
    {
        case "/createqueue":
            await CreateQueueCommand(args, msg);
            break;
        case "/showallqueues":
            await ShowAllQueuesCommand(msg);
            break;
        case "/showmyqueues":
            await ShowMyQueuesCommand(msg);
            break;
        case "/setlocale":
            await SetLocaleCommand(args, msg);
            break;
        case "/usage":
            await UsageCommand(msg);
            break;
        default:
            Console.WriteLine("Undefined command");
            break;
    }
}

async Task OnUpdate(Telegram.Bot.Types.Update update)
{
    try
    {
        switch (update)
        {
            case { CallbackQuery: { } callbackQuery }: await OnCallbackQuery(callbackQuery); break;
            //case { PollAnswer: { } pollAnswer }: await OnPollAnswer(pollAnswer); break;
            default: Console.WriteLine($"Received unhandled update {update.Type}"); break;
        };
    }
    catch (Exception e) { Console.WriteLine(e); }
}

async Task OnCallbackQuery(CallbackQuery callbackQuery)
{
    if (callbackQuery.Data == null) { return; }
    if (callbackQuery.Data.StartsWith("Queue")) { await HandleQueueButton(callbackQuery); return; }
}
#endregion