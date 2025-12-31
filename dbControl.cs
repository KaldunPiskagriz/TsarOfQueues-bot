using Microsoft.EntityFrameworkCore;

public class QueueUserEntry
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public string UserName { get; set; }
    public int QueueId { get; set; }
    public int QueuePosition { get; set; }
    public QueueDataEntry? QueueData { get; set; }
}

public class QueueDataEntry
{
    public int Id { get; set; }
    public int QueueId { get; set; }
    public string Name { get; set; }
    public int QueueMessageId { get; set; }
    public long QueueChatId { get; set; }
    public DateTime ExpireDate { get; set; }
    public ICollection<QueueUserEntry> Users { get; set; }
}

public class QueueChatLocaleEntry
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public string LocaleName { get; set; }
}

public class ApplicationContext : DbContext
{
    public DbSet<QueueUserEntry> Queues => Set<QueueUserEntry>();
    public DbSet<QueueDataEntry> QueueDatas => Set<QueueDataEntry>();
    public DbSet<QueueChatLocaleEntry> QueueChatLocales => Set<QueueChatLocaleEntry>();

    public ApplicationContext()
    {
        Database.EnsureCreated();
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite("Data Source=queues.db");
    }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QueueUserEntry>()
            .HasOne(p => p.QueueData)
            .WithMany(t => t.Users)
            .HasForeignKey(p => p.QueueId)
            .HasPrincipalKey(t=>t.QueueId);
    }
}
