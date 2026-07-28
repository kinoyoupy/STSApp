using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using STSApp.Backend.Domain.Entities;
using STSApp.Contracts.Enums;

namespace STSApp.Backend.Data;

/// <summary>
/// STSシステムのMySQL接続をまとめるDbContextです。
/// 設計書の conversations / conversation_turns / audio_files / turn_events に対応します。
/// </summary>
public sealed class StsDbContext : DbContext
{
    public StsDbContext(DbContextOptions<StsDbContext> options)
        : base(options)
    {
    }

    public DbSet<ConversationEntity> Conversations => Set<ConversationEntity>();
    public DbSet<ConversationTurnEntity> ConversationTurns => Set<ConversationTurnEntity>();
    public DbSet<AudioFileEntity> AudioFiles => Set<AudioFileEntity>();
    public DbSet<TurnEventEntity> TurnEvents => Set<TurnEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureConversations(modelBuilder);
        ConfigureConversationTurns(modelBuilder);
        ConfigureAudioFiles(modelBuilder);
        ConfigureTurnEvents(modelBuilder);
    }

    private static void ConfigureConversations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConversationEntity>(entity =>
        {
            entity.ToTable("conversations");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("char(36)");

            entity.Property(x => x.Title)
                .HasColumnName("title")
                .HasMaxLength(255)
                .IsRequired();

            entity.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("datetime(6)");

            entity.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("datetime(6)");
        });
    }

    private static void ConfigureConversationTurns(ModelBuilder modelBuilder)
    {
        var statusConverter = CreateEnumConverter<TurnStatus>();
        var stageConverter = CreateNullableEnumConverter<ProcessingStage>();

        modelBuilder.Entity<ConversationTurnEntity>(entity =>
        {
            entity.ToTable("conversation_turns");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("char(36)");

            entity.Property(x => x.ConversationId)
                .HasColumnName("conversation_id")
                .HasColumnType("char(36)");

            entity.Property(x => x.UserText)
                .HasColumnName("user_text")
                .HasColumnType("text");

            entity.Property(x => x.AssistantText)
                .HasColumnName("assistant_text")
                .HasColumnType("text");

            entity.Property(x => x.Status)
                .HasColumnName("status")
                .HasColumnType("enum('processing','completed','failed')")
                .HasConversion(statusConverter);

            entity.Property(x => x.ErrorStage)
                .HasColumnName("error_stage")
                .HasColumnType("enum('upload','stt','gemini','tts','database')")
                .HasConversion(stageConverter);

            entity.Property(x => x.ErrorMessage)
                .HasColumnName("error_message")
                .HasColumnType("text");

            entity.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("datetime(6)");

            entity.Property(x => x.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("datetime(6)");

            entity.HasIndex(x => x.ConversationId);
            entity.HasIndex(x => x.CreatedAt);

            entity.HasOne<ConversationEntity>()
                .WithMany()
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAudioFiles(ModelBuilder modelBuilder)
    {
        var kindConverter = CreateEnumConverter<AudioFileKind>();

        modelBuilder.Entity<AudioFileEntity>(entity =>
        {
            entity.ToTable("audio_files");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .HasColumnName("id")
                .HasColumnType("char(36)");

            entity.Property(x => x.ConversationTurnId)
                .HasColumnName("conversation_turn_id")
                .HasColumnType("char(36)");

            entity.Property(x => x.Kind)
                .HasColumnName("kind")
                .HasColumnType("enum('input','output')")
                .HasConversion(kindConverter);

            entity.Property(x => x.FilePath)
                .HasColumnName("file_path")
                // file_path は Backend が生成する storage/audio/... のASCIIパスとして扱います。
                // utf8mb4 のまま 1024 文字へ UNIQUE を貼ると MySQL のキー長上限に当たるため、
                // このカラムだけ ASCII にしています。
                .HasCharSet("ascii")
                .HasMaxLength(1024)
                .IsRequired();

            entity.Property(x => x.MimeType)
                .HasColumnName("mime_type")
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(x => x.FileSizeBytes)
                .HasColumnName("file_size_bytes");

            entity.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("datetime(6)");

            entity.HasIndex(x => x.ConversationTurnId);

            // 同じ実ファイルを複数レコードが指さないようにします。
            // conversation_turn_id + kind は、将来1ターンに複数音声を持てるよう一意制約にしません。
            entity.HasIndex(x => x.FilePath).IsUnique();

            entity.HasOne<ConversationTurnEntity>()
                .WithMany()
                .HasForeignKey(x => x.ConversationTurnId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureTurnEvents(ModelBuilder modelBuilder)
    {
        var stageConverter = CreateEnumConverter<ProcessingStage>();
        var eventTypeConverter = CreateEnumConverter<TurnEventType>();

        modelBuilder.Entity<TurnEventEntity>(entity =>
        {
            entity.ToTable("turn_events");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Id)
                .HasColumnName("id")
                .ValueGeneratedOnAdd();

            entity.Property(x => x.ConversationTurnId)
                .HasColumnName("conversation_turn_id")
                .HasColumnType("char(36)");

            entity.Property(x => x.Stage)
                .HasColumnName("stage")
                .HasColumnType("enum('upload','stt','gemini','tts','database')")
                .HasConversion(stageConverter);

            entity.Property(x => x.EventType)
                .HasColumnName("event_type")
                .HasColumnType("enum('started','completed','failed','info')")
                .HasConversion(eventTypeConverter);

            entity.Property(x => x.Message)
                .HasColumnName("message")
                .HasColumnType("text");

            entity.Property(x => x.MetadataJson)
                .HasColumnName("metadata_json")
                .HasColumnType("json");

            entity.Property(x => x.DurationMs)
                .HasColumnName("duration_ms");

            entity.Property(x => x.OccurredAt)
                .HasColumnName("occurred_at")
                .HasColumnType("datetime(6)");

            entity.HasIndex(x => x.ConversationTurnId);
            entity.HasIndex(x => x.OccurredAt);

            entity.HasOne<ConversationTurnEntity>()
                .WithMany()
                .HasForeignKey(x => x.ConversationTurnId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static ValueConverter<TEnum, string> CreateEnumConverter<TEnum>()
        where TEnum : struct, Enum
    {
        return new ValueConverter<TEnum, string>(
            value => ToDatabaseValue(value),
            value => FromDatabaseValue<TEnum>(value));
    }

    private static ValueConverter<TEnum?, string?> CreateNullableEnumConverter<TEnum>()
        where TEnum : struct, Enum
    {
        return new ValueConverter<TEnum?, string?>(
            value => value.HasValue ? ToDatabaseValue(value.Value) : null,
            value => string.IsNullOrWhiteSpace(value) ? null : FromDatabaseValue<TEnum>(value));
    }

    private static string ToDatabaseValue<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        return value.ToString() switch
        {
            nameof(TurnStatus.Processing) => "processing",
            nameof(TurnStatus.Completed) => "completed",
            nameof(TurnStatus.Failed) => "failed",
            nameof(ProcessingStage.Upload) => "upload",
            nameof(ProcessingStage.Stt) => "stt",
            nameof(ProcessingStage.Gemini) => "gemini",
            nameof(ProcessingStage.Tts) => "tts",
            nameof(ProcessingStage.Database) => "database",
            nameof(TurnEventType.Started) => "started",
            nameof(TurnEventType.Info) => "info",
            nameof(AudioFileKind.Input) => "input",
            nameof(AudioFileKind.Output) => "output",
            _ => throw new InvalidOperationException($"Unsupported enum value: {typeof(TEnum).Name}.{value}")
        };
    }

    private static TEnum FromDatabaseValue<TEnum>(string value)
        where TEnum : struct, Enum
    {
        var enumValue = value switch
        {
            "processing" => nameof(TurnStatus.Processing),
            "completed" => typeof(TEnum) == typeof(TurnEventType)
                ? nameof(TurnEventType.Completed)
                : nameof(TurnStatus.Completed),
            "failed" => typeof(TEnum) == typeof(TurnEventType)
                ? nameof(TurnEventType.Failed)
                : nameof(TurnStatus.Failed),
            "upload" => nameof(ProcessingStage.Upload),
            "stt" => nameof(ProcessingStage.Stt),
            "gemini" => nameof(ProcessingStage.Gemini),
            "tts" => nameof(ProcessingStage.Tts),
            "database" => nameof(ProcessingStage.Database),
            "started" => nameof(TurnEventType.Started),
            "info" => nameof(TurnEventType.Info),
            "input" => nameof(AudioFileKind.Input),
            "output" => nameof(AudioFileKind.Output),
            _ => throw new InvalidOperationException($"Unsupported database enum value: {value}")
        };

        return Enum.Parse<TEnum>(enumValue);
    }
}
