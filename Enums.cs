namespace QueueKingBot;

public enum LocaleKeys
{
    QueueOutput_Queue,
    QueueOutput_QueueIsEmpty,
    QueueOutput_EnterQueue,
    QueueOutput_LeaveQueue,
    QueueOutput_LetAhead,
    QueueOutput_Expires,
    QueueOutput_Expired,
    Command_NoQueuesInChat,
    Command_HereIsQueue,
    Command_OnlyInPerson,
    Command_YouAreNotInAnyQueue,
    Command_LocaleNotFound,
    Command_LocaleSet,
    Command_EmptyQueueName,
    Command_InvalidDateTime,
    Command_InvalidTimeSpan,
    Command_NegativeWaitTime,
    Callback_YouAreInQueue,
    Callback_QueueEntered,
    Callback_NotInQueue,
    Callback_QueueLeft,
    Callback_NobodyToLet,
    Callback_QueueIsDeleted,
    Usage_Title,
    Usage_Name,
    Usage_CreateQueueUsage,
    Usage_ShowAllQueues,
    Usage_ShowMyQueues,
    Usage_SetLocale,
    Usage_Usage
}

public enum CommandArgumentType
{
    QueueName,
    ExpireDate,
    ExpireTime,
    LocaleName
}

public enum CallbackQueryType
{
    None,
    QueueEnter,
    QueueLeave,
    QueueLet
}

public enum BotCommandType
{
    None,
    Invalid,
    CreateQueue,
    ShowAllQueues,
    ShowMyQueues,
    Usage,
    SetLocale
}