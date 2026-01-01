namespace QueueKingBot;

public class BotCommand
{
    public BotCommandType type = BotCommandType.None;
    public Dictionary<CommandArgumentType, string> arguments = new();

    public BotCommand() { }

    public BotCommand(string command)
    {
        CreateCommandFromString(command);
    }

    public bool CreateCommandFromString(string command)
    {
        string[] commandStrings = command.Trim().Split(' ');
        if (commandStrings.Length == 0 || !Enum.TryParse(commandStrings[0].Substring(1), out type))
        {
            SetInvalidCommand();
            return false;
        }
        if (type == BotCommandType.CreateQueue)
        {
            string[] nameParts = commandStrings[1..].TakeWhile(item => !item.StartsWith("--")).ToArray();
            arguments.Add(CommandArgumentType.QueueName, string.Join(" ", nameParts));
        }
        if (type == BotCommandType.SetLocale)
        {
            arguments.Add(CommandArgumentType.LocaleName, commandStrings[1]);
        }
        string[] commandArgs = commandStrings.Where(item => item.StartsWith("--")).ToArray();
        foreach (var item in commandArgs)
        {
            string[] argParts = item.Split('=');
            if (argParts.Length != 2)
            {
                SetInvalidCommand();
                return false;
            }
            switch (argParts[0])
            {
                case "--date":
                    arguments.Add(CommandArgumentType.ExpireDate, argParts[1]);
                    break;
                case "--time":
                    arguments.Add(CommandArgumentType.ExpireTime, argParts[1]);
                    break;
            }
        }
        return true;
    }

    void SetInvalidCommand()
    {
        type = BotCommandType.Invalid;
        arguments.Clear();
    }
}