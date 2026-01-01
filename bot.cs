using System.Text;
using QueueKingBot;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using BotCommand = QueueKingBot.BotCommand;

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
    StringBuilder sb = new StringBuilder();
    sb.Append($"{LocalesTextHolder.GetText(queueId, LocaleKeys.QueueOutput_Queue)}: <b>{GetQueueName(queueId)}</b>\n");
    QueueDataEntry entry = FindQueue(queueId);
    if (entry.ExpireDate < DateTime.Now)
    {
        sb.Append($"{LocalesTextHolder.GetText(queueId, LocaleKeys.QueueOutput_Expired)}\n\n");
    }
    else
    {
        sb.Append($"{LocalesTextHolder.GetText(queueId, LocaleKeys.QueueOutput_Expires)}: {entry.ExpireDate:G}\n\n");
    }

    ICollection<QueueUserEntry>? entries = entry.Users;
    if (entries == null || entries.Count == 0)
    {
        sb.Append($"<i>{LocalesTextHolder.GetText(queueId, LocaleKeys.QueueOutput_QueueIsEmpty)}</i>");
        return sb.ToString();
    }
    foreach (var e in entries.OrderBy(item => item.QueuePosition))
    {
        sb.Append($"{e.QueuePosition}. {e.UserName}\n");
    }
    return sb.ToString();
}

async Task UpdateQueueMessage(ChatId chatId, int messageId, int queueId)
{
    await bot.EditMessageText(chatId,
                messageId,
                GetBeautifulQueueStringOutput(queueId),
                parseMode: ParseMode.Html,
                replyMarkup: FindQueue(queueId).ExpireDate < DateTime.Now ? null : 
                new InlineKeyboardButton[][]
                {
                    [(LocalesTextHolder.GetText(queueId, LocaleKeys.QueueOutput_EnterQueue), $"{CallbackQueryType.QueueEnter}:{queueId.ToString()}")],
                    [(LocalesTextHolder.GetText(queueId, LocaleKeys.QueueOutput_LeaveQueue), $"{CallbackQueryType.QueueLeave}:{queueId.ToString()}")],
                    [(LocalesTextHolder.GetText(queueId, LocaleKeys.QueueOutput_LetAhead), $"{CallbackQueryType.QueueLet}:{queueId.ToString()}")]
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
    if (entry != null) return entry;
    Console.WriteLine($"Cannot find queue: queue with id {queueId} doesn't exist");
    return new QueueDataEntry();
}

async Task DelayedDeleteQueue(TimeSpan delay, int queueId)
{
    Console.WriteLine($"Waiting {delay} to delete queue {queueId}");
    if(delay > TimeSpan.Zero) await Task.Delay(delay);
    Console.WriteLine($"Deleting queue {queueId}");
    QueueDataEntry entry = FindQueue(queueId);
    try
    {
        await UpdateQueueMessage(entry.QueueChatId, entry.QueueMessageId, entry.QueueId);
    }
    catch (ApiRequestException) { }
    db.QueueDatas.Remove(entry);
    if(entry.Users != null) db.RemoveRange(entry.Users.ToArray<object>());
    await db.SaveChangesAsync();
}
#endregion

#region Bot commands
async Task HandleQueueButton(CallbackQuery callbackQuery)
{
    Console.WriteLine($"Received callback query: {callbackQuery.Data}");
    if(callbackQuery.Data == null || callbackQuery.Message == null) { return; }
    string[] queryStrings = callbackQuery.Data.Split(':');
    int queueId = int.Parse(queryStrings[1]);
    if (!Enum.TryParse(queryStrings[0], out CallbackQueryType queryType)) { queryType = CallbackQueryType.None; }
    QueueDataEntry thisQueue = FindQueue(queueId);
    if (thisQueue == new QueueDataEntry())
    {
        await bot.AnswerCallbackQuery(callbackQuery.Id,
            $"{LocalesTextHolder.GetText(callbackQuery.Message.Chat.Id, LocaleKeys.Callback_QueueIsDeleted)}");
        return;
    }
    switch (queryType)
    {
        case CallbackQueryType.QueueEnter:
            QueueUserEntry? checkUser = db.Queues.FirstOrDefault(item => item.UserId == callbackQuery.From.Id && item.QueueId == queueId);
            if (checkUser != null)
            {
                await bot.AnswerCallbackQuery(callbackQuery.Id, $"{LocalesTextHolder.GetText(callbackQuery.Message.Chat.Id, LocaleKeys.Callback_YouAreInQueue)} {GetQueueName(queueId)}");
                return;
            }
            int position = 0;
            if (thisQueue.Users is { Count: > 0 })
            {
                position = thisQueue.Users.Max(item => item.QueuePosition);
            }
            QueueUserEntry newUserEntry = new QueueUserEntry
            {
                UserId = callbackQuery.From.Id,
                UserName = callbackQuery.From.FirstName + " " + callbackQuery.From.LastName,
                QueueId = queueId,
                QueuePosition = position + 1,
            };
            db.Add(newUserEntry);
            await db.SaveChangesAsync();
            await UpdateQueueMessage(callbackQuery.Message.Chat.Id, callbackQuery.Message.Id, queueId);
            await bot.AnswerCallbackQuery(callbackQuery.Id, $"{LocalesTextHolder.GetText(callbackQuery.Message.Chat.Id, LocaleKeys.Callback_QueueEntered)} {GetQueueName(queueId)}");
            Console.WriteLine($"user {callbackQuery.From.Id} added to queue {queueId}");
            break;
        case CallbackQueryType.QueueLeave:
            QueueUserEntry? userEntry = db.Queues.FirstOrDefault(item => item.UserId == callbackQuery.From.Id && item.QueueId == queueId);
            if (userEntry == null)
            {
                await bot.AnswerCallbackQuery(callbackQuery.Id, $"{LocalesTextHolder.GetText(callbackQuery.Message.Chat.Id, LocaleKeys.Callback_NotInQueue)} {GetQueueName(queueId)}");
            }
            else
            {
                db.Remove(userEntry);
                await db.SaveChangesAsync();
                await UpdateQueueMessage(callbackQuery.Message.Chat.Id, callbackQuery.Message.Id, queueId);
                await bot.AnswerCallbackQuery(callbackQuery.Id, $"{LocalesTextHolder.GetText(callbackQuery.Message.Chat.Id, LocaleKeys.Callback_QueueLeft)} {GetQueueName(queueId)}");
                Console.WriteLine($"user {callbackQuery.From.Id} removed from queue {queueId}");
            }
            break;
        case CallbackQueryType.QueueLet:
            var initialUser = db.Queues.FirstOrDefault(item => item.UserId == callbackQuery.From.Id && item.QueueId == queueId);
            if (initialUser == null)
            {
                await bot.AnswerCallbackQuery(callbackQuery.Id, $"{LocalesTextHolder.GetText(callbackQuery.Message.Chat.Id, LocaleKeys.Callback_NotInQueue)} {GetQueueName(queueId)}");
                return;
            }
            var nextUser = db.Queues.FirstOrDefault(item => item.QueuePosition == initialUser.QueuePosition + 1 && item.QueueId == queueId);
            if (nextUser == null)
            {
                await bot.AnswerCallbackQuery(callbackQuery.Id, $"{LocalesTextHolder.GetText(callbackQuery.Message.Chat.Id, LocaleKeys.Callback_NobodyToLet)} {GetQueueName(queueId)}");
                return;
            }
            Console.WriteLine("trying to swap users");
            (initialUser.QueuePosition, nextUser.QueuePosition) = (nextUser.QueuePosition, initialUser.QueuePosition);
            db.UpdateRange(initialUser, nextUser);
            await db.SaveChangesAsync();
            await UpdateQueueMessage(callbackQuery.Message.Chat.Id, callbackQuery.Message.Id, queueId);
            Console.WriteLine($"swapped data of users {initialUser.UserId} and {nextUser.UserId}");
            break;
    }
}
async Task CreateQueueCommand(Dictionary<CommandArgumentType, string> args, Message msg)
{
    Console.WriteLine("Executing command \"/createqueue\"");
    if (args[CommandArgumentType.QueueName] == "")
    {
        await bot.SendMessage(msg.Chat.Id, $"{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Command_EmptyQueueName)}");
        return;
    }
    DateTime expireTime = DateTime.MinValue;
    if(args.Count > 1)
    {
        if (args.TryGetValue(CommandArgumentType.ExpireDate, out string? date))
        {
            if(!DateTime.TryParse(date, out expireTime))
            {
                await bot.SendMessage(msg.Chat.Id, $"{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Command_InvalidDateTime)}");
                return;
            }
            if (expireTime < DateTime.Now)
            {
                await bot.SendMessage(msg.Chat.Id, $"{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Command_NegativeWaitTime)}");
                return;
            }
            if(expireTime is { Hour: 0, Minute: 0, Second: 0 })
            {
                expireTime = expireTime.AddHours(DateTime.Now.Hour).AddMinutes(DateTime.Now.Minute).AddSeconds(DateTime.Now.Second);
            }
        }
        if (args.TryGetValue(CommandArgumentType.ExpireTime, out string? time))
        {
            if (!TimeSpan.TryParse(time, out TimeSpan expireSpan))
            {
                await bot.SendMessage(msg.Chat.Id, $"{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Command_InvalidTimeSpan)}");
                return;
            }
            expireTime = DateTime.Now.Add(expireSpan);
        }
    }
    int queueId = GetNextQueueId();
    Message queueMsg = await bot.SendMessage(msg.Chat.Id, "loading...");
    QueueDataEntry entry = new QueueDataEntry()
    {
        QueueId = queueId,
        Name = args[CommandArgumentType.QueueName],
        ExpireDate = expireTime == DateTime.MinValue ? DateTime.Now.AddDays(7) : expireTime,
        QueueChatId = msg.Chat.Id,
        QueueMessageId = queueMsg.Id
    };
    db.Add(entry);
    await db.SaveChangesAsync();
    await UpdateQueueMessage(msg.Chat.Id, queueMsg.Id, queueId);
    Console.WriteLine($"created queue \"{entry.Name}\":{queueId}, expires: {entry.ExpireDate}");
    _ = DelayedDeleteQueue(entry.ExpireDate.Subtract(DateTime.Now), queueId);
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
            ReplyParameters parameters = new ReplyParameters
            {
                ChatId = queue.QueueChatId,
                MessageId = queue.QueueMessageId
            };
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
    StringBuilder sb = new StringBuilder();
    sb.Append($"{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_Title)}");
    sb.Append($"\n\n/CreateQueue <{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_Name)}> - {LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_CreateQueueUsage)}");
    sb.Append($"\n\n/ShowAllQueues - {LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_ShowAllQueues)}");
    sb.Append($"\n\n/ShowMyQueues - {LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_ShowMyQueues)}");
    sb.Append($"\n\n/Usage - {LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_Usage)}");
    sb.Append($"\n\n/SetLocale <{LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_Name)}> - {LocalesTextHolder.GetText(msg.Chat.Id, LocaleKeys.Usage_SetLocale)}");
    await bot.SendMessage(msg.Chat.Id, sb.ToString());
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
        BotCommand command = new BotCommand(msg.Text);
        await OnCommand(command, msg);
    }
}

async Task OnCommand(BotCommand command, Message msg)
{
    switch (command.type)
    {
        case BotCommandType.CreateQueue:
            await CreateQueueCommand(command.arguments, msg);
            break;
        case BotCommandType.ShowAllQueues:
            await ShowAllQueuesCommand(msg);
            break;
        case BotCommandType.ShowMyQueues:
            await ShowMyQueuesCommand(msg);
            break;
        case BotCommandType.SetLocale:
            await SetLocaleCommand(command.arguments[CommandArgumentType.LocaleName], msg);
            break;
        case BotCommandType.Usage:
            await UsageCommand(msg);
            break;
        default:
            Console.WriteLine("Undefined command");
            break;
    }
}

async Task OnUpdate(Update update)
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
    if (callbackQuery.Data.StartsWith("Queue")) { await HandleQueueButton(callbackQuery); }
}
#endregion