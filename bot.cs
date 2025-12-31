using QueueKingBot;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

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

ApplicationContext db = new ApplicationContext();

HashSet<int> waitQueueDeletion = new();
DateTime nowTime = DateTime.Now;
foreach (QueueDataEntry item in db.QueueDatas)
{
    _ = DelayedDeleteQueue(item.ExpireDate.Subtract(nowTime), item.QueueId);
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
    if (!db.QueueDatas.Any()) { return 0; }
    return db.QueueDatas.Max(item => item.QueueId) + 1;
}

string GetBeautifulQueueStringOutput(int queueId)
{
    string output = $"{LocalesTextHolder.GetText(queueId, LocaleKeys.QueueOutput_Queue)}: <b>{GetQueueName(queueId)}</b>\n";
    QueueDataEntry entry = FindQueue(queueId);
    if (entry.ExpireDate < DateTime.Now)
    {
        output += $"{LocalesTextHolder.GetText(queueId, LocaleKeys.QueueOutput_Expired)}\n\n";
    }
    else
    {
        DateTime expireTime = entry.ExpireDate;
        output += $"{LocalesTextHolder.GetText(queueId, LocaleKeys.QueueOutput_Expires)}: {string.Format("{0:d2}", expireTime.Day)}.{string.Format("{0:d2}", expireTime.Month)}.{string.Format("{0:d4}", expireTime.Year)} {string.Format("{0:d2}", expireTime.Hour)}:{string.Format("{0:d2}", expireTime.Minute)}\n\n";
    }
    List<QueueUserEntry> entries = db.Queues.Where(item => item.QueueId == queueId).ToList();
    if(entries.Count == 0)
    {
        output += $"<i>{LocalesTextHolder.GetText(queueId, LocaleKeys.QueueOutput_QueueIsEmpty)}</i>";
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
                replyMarkup: FindQueue(queueId).ExpireDate > DateTime.Now ? null : 
                new InlineKeyboardButton[][]
                {
                    [(LocalesTextHolder.GetText(queueId, LocaleKeys.QueueOutput_EnterQueue), $"QueueEnter:{queueId.ToString()}")],
                    [(LocalesTextHolder.GetText(queueId, LocaleKeys.QueueOutput_LeaveQueue), $"QueueLeave:{queueId.ToString()}")],
                    [(LocalesTextHolder.GetText(queueId, LocaleKeys.QueueOutput_LetAhead), $"QueueLet:{queueId.ToString()}")]
                });
}

async Task<Message> SendQueueMessage(ChatId chatId, int queueId)
{
    Message queueMsg = await bot.SendMessage(chatId, $"{LocalesTextHolder.GetText(chatId, LocaleKeys.QueueOutput_Queue)}: <b>{GetQueueName(queueId)}</b>\n\n");
    await UpdateQueueMessage(chatId, queueMsg.Id, queueId);
    return queueMsg;
}

string GetQueueName(int queueId)
{
    return FindQueue(queueId).Name;
}

QueueDataEntry FindQueue(int queueId)
{
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
    if(delay > TimeSpan.Zero) await Task.Delay(delay);
    Console.WriteLine($"Deleting queue {queueId}");
    QueueDataEntry entry = FindQueue(queueId);
    QueueUserEntry[] users = db.Queues.Where(item => item.QueueId == queueId).ToArray();
    try
    {
        await UpdateQueueMessage(entry.QueueChatId, entry.QueueMessageId, entry.QueueId);
    }
    catch (ApiRequestException) { }
    db.QueueDatas.Remove(entry);
    db.RemoveRange(users);
    await db.SaveChangesAsync();
    waitQueueDeletion.Remove(queueId);
}
#endregion

#region Bot commands
async Task HandleQueueButton(CallbackQuery callbackQuery)
{
    Console.WriteLine($"Received callback querry: {callbackQuery.Data}");
    if(callbackQuery.Data == null || callbackQuery.Message == null) { return; }
    int queueId = int.Parse(callbackQuery.Data[(callbackQuery.Data.IndexOf(':') + 1)..]);
    if (db.QueueDatas.FirstOrDefault(item => item.QueueId == queueId) == null)
    {
        await bot.AnswerCallbackQuery(callbackQuery.Id, $"{LocalesTextHolder.GetText(callbackQuery.Message.Chat.Id, LocaleKeys.Callback_QueueIsDeleted)}");
        return;
    }
    if (callbackQuery.Data.StartsWith("QueueEnter:"))
    {
        QueueUserEntry? checkUser = db.Queues.FirstOrDefault(item => item.UserId == callbackQuery.From.Id && item.QueueId == queueId);
        if (checkUser != null)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, $"{LocalesTextHolder.GetText(callbackQuery.Message.Chat.Id, LocaleKeys.Callback_YouAreInQueue)} {GetQueueName(queueId)}");
            return;
        }
        QueueUserEntry entry = new QueueUserEntry()
        {
            UserId = callbackQuery.From.Id,
            UserName = callbackQuery.From.FirstName + " " + callbackQuery.From.LastName,
            QueueId = queueId,
            QueuePosition = db.Queues.Where(item => item.QueueId == queueId).Max(item => item.QueuePosition) + 1,
        };
        db.Add(entry);
        await db.SaveChangesAsync();
        await UpdateQueueMessage(callbackQuery.Message.Chat.Id, callbackQuery.Message.Id, queueId);
        await bot.AnswerCallbackQuery(callbackQuery.Id, $"{LocalesTextHolder.GetText(callbackQuery.Message.Chat.Id, LocaleKeys.Callback_QueueEntered)} {GetQueueName(queueId)}");
        Console.WriteLine($"user {callbackQuery.From.Id} added to queue {queueId}");
    }
    else if (callbackQuery.Data.StartsWith("QueueLeave:"))
    {
        QueueUserEntry? entry = db.Queues.FirstOrDefault(item => item.UserId == callbackQuery.From.Id && item.QueueId == queueId);
        if (entry == null)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, $"{LocalesTextHolder.GetText(callbackQuery.Message.Chat.Id, LocaleKeys.Callback_NotInQueue)} {GetQueueName(queueId)}");
        }
        else
        {
            db.Remove(entry);
            await db.SaveChangesAsync();
            await UpdateQueueMessage(callbackQuery.Message.Chat.Id, callbackQuery.Message.Id, queueId);
            await bot.AnswerCallbackQuery(callbackQuery.Id, $"{LocalesTextHolder.GetText(callbackQuery.Message.Chat.Id, LocaleKeys.Callback_QueueLeft)} {GetQueueName(queueId)}");
            Console.WriteLine($"user {callbackQuery.From.Id} removed from queue {queueId}");
        }
    }
    else if (callbackQuery.Data.StartsWith("QueueLet:"))
    {
        QueueUserEntry? initialUser, nextUser;
        initialUser = db.Queues.FirstOrDefault(item => item.UserId == callbackQuery.From.Id && item.QueueId == queueId);
        if (initialUser == null)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, $"{LocalesTextHolder.GetText(callbackQuery.Message.Chat.Id, LocaleKeys.Callback_NotInQueue)} {GetQueueName(queueId)}");
            return;
        }
        nextUser = db.Queues.FirstOrDefault(item => item.QueuePosition == initialUser.QueuePosition + 1 && item.QueueId == queueId);
        if (nextUser == null)
        {
            await bot.AnswerCallbackQuery(callbackQuery.Id, $"{LocalesTextHolder.GetText(callbackQuery.Message.Chat.Id, LocaleKeys.Callback_NobodyToLet)} {GetQueueName(queueId)}");
            return;
        }
        Console.WriteLine("trying to swap users");
        (initialUser.QueuePosition, nextUser.QueuePosition) = (nextUser.QueuePosition, initialUser.QueuePosition);
        db.UpdateRange([initialUser, nextUser]);
        await db.SaveChangesAsync();
        await UpdateQueueMessage(callbackQuery.Message.Chat.Id, callbackQuery.Message.Id, queueId);
        Console.WriteLine($"swapped data of users {initialUser.UserId} and {nextUser.UserId}");
    }
}
async Task CreateQueueCommand(string args, Message msg)
{
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
        await bot.SendMessage(msg.Chat.Id, $"{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Command_EmptyQueueName)}");
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
                await bot.SendMessage(msg.Chat.Id, $"{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Command_InvalidDateTime)}");
                return;
            }
            if (expireTime < now)
            {
                await bot.SendMessage(msg.Chat.Id, $"{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Command_NegativeWaitTime)}");
                return;
            }
            if(expireTime is { Hour: 0, Minute: 0, Second: 0 })
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
                await bot.SendMessage(msg.Chat.Id, $"{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Command_InvalidTimeSpan)}");
                return;
            }
            else
            {
                expireTime = now.Add(expireSpan);
            }
        }
    }
    int queueId = GetNextQueueId();
    Message queueMsg = await bot.SendMessage(msg.Chat.Id, "loading...");
    QueueDataEntry entry = new QueueDataEntry()
    {
        QueueId = queueId,
        Name = queueName,
        ExpireDate = expireTime == DateTime.MinValue ? now.AddDays(7) : expireTime,
        QueueChatId = msg.Chat.Id,
        QueueMessageId = queueMsg.Id
    };
    db.Add(entry);
    await db.SaveChangesAsync();
    await UpdateQueueMessage(msg.Chat.Id, queueMsg.Id, queueId);
    Console.WriteLine($"created queue \"{queueName}\":{queueId}, expires: {entry.ExpireDate}");
    _ = DelayedDeleteQueue(entry.ExpireDate.Subtract(now), queueId);
}

async Task ShowAllQueuesCommand(Message msg)
{
    Console.WriteLine("Executing /showallqueues command");
    if (!db.QueueDatas.Any(item => item.QueueChatId == msg.Chat.Id)) { await bot.SendMessage(msg.Chat.Id, LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Command_NoQueuesInChat)); return; }
    foreach (var queue in db.QueueDatas)
    {
        if (queue.QueueChatId != msg.Chat.Id) continue;
        try
        {
            ReplyParameters parameters = new ReplyParameters();
            parameters.ChatId = queue.QueueChatId;
            parameters.MessageId = queue.QueueMessageId;
            await bot.SendMessage(msg.Chat.Id, $"{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Command_HereIsQueue)} {GetQueueName(queue.QueueId)}", replyParameters: parameters);
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
                await db.SaveChangesAsync();
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
    if (msg.Chat.Type != ChatType.Private) { await bot.SendMessage(msg.Chat.Id, LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Command_OnlyInPerson)); return; }
    List<int> queues = db.Queues.Where(item => item.UserId == msg.From.Id).Select(item => item.QueueId).ToList();
    if (!queues.Any()) { await bot.SendMessage(msg.Chat.Id, LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Command_YouAreNotInAnyQueue)); return; }
    foreach (int id in queues)
    {
        QueueDataEntry? myQueue = db.QueueDatas.FirstOrDefault(item => item.QueueId == id);
        if (myQueue == null) { Console.WriteLine($"There is no queue with id {id} in database"); continue; }
        string link = $"$\"https://t.me/c/{myQueue.QueueChatId.ToString().Substring(4)}/{myQueue.QueueMessageId}\"";
        string preparedQueueLink = $"{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.QueueOutput_Queue)} {myQueue.Name} {link}";
        await bot.SendMessage(msg.Chat.Id, preparedQueueLink);
        await bot.ForwardMessage(msg.Chat.Id, myQueue.QueueChatId, myQueue.QueueMessageId);
    }
}

async Task SetLocaleCommand(string args, Message msg)
{
    Console.WriteLine("Executing /setlocale command");
    if (!LocalesTextHolder.HasLocale(args))
    {
        await bot.SendMessage(msg.Chat.Id, $"{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Command_LocaleNotFound)} {args}");
        return;
    }
    QueueChatLocaleEntry localeEntry = db.QueueChatLocales.First(item => item.ChatId == msg.Chat.Id);
    localeEntry.LocaleName = args;
    db.Update(localeEntry);
    await db.SaveChangesAsync();
    await bot.SendMessage(msg.Chat.Id, $"{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Command_LocaleSet)} {args}");
}

async Task UsageCommand(Message msg)
{
    Console.WriteLine("Executing /usage command");
    string usageMessage = $"{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_Title)}" +
                $"\n\n/createqueue <{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_Name)}> - {LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_CreateQueueUsage)}" +
                $"\n\n/showallqueues - {LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_ShowAllQueues)}" +
                $"\n\n/showmyqueues - {LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_ShowMyQueues)}" +
                $"\n\n/usage - {LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_Usage)}" +
                $"\n\n/setlocale <{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_Name)}> - {LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_SetLocale)}";
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