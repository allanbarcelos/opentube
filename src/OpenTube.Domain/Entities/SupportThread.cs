using OpenTube.Domain.Enums;

namespace OpenTube.Domain.Entities;

/// <summary>
/// Uma conversa sobre um vídeo, entre quem assiste e a administração. Não é comentário
/// público: só o autor e os administradores enxergam, o que muda tudo sobre o que as pessoas
/// se sentem à vontade para escrever.
/// </summary>
public class SupportThread
{
    private readonly List<SupportMessage> _messages = [];

    private SupportThread() { }

    public Guid Id { get; private set; }
    public Guid VideoId { get; private set; }

    /// <summary>Quem abriu a conversa.</summary>
    public Guid UserId { get; private set; }

    public SupportStatus Status { get; private set; }

    /// <summary>Instante do vídeo a que a pergunta se refere, quando houver.</summary>
    public double? TimestampSeconds { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LastMessageAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>Quando o autor viu a última resposta.</summary>
    public DateTimeOffset? ReadByUserAt { get; private set; }

    /// <summary>Quando a administração viu a última mensagem do autor.</summary>
    public DateTimeOffset? ReadByAdminAt { get; private set; }

    public IReadOnlyList<SupportMessage> Messages => _messages;

    public bool IsClosed => Status is SupportStatus.Closed;

    /// <summary>Há mensagem do autor ainda não lida pela administração.</summary>
    public bool NeedsAdminAttention =>
        Status is SupportStatus.Open && (ReadByAdminAt is null || ReadByAdminAt < LastMessageAt);

    /// <summary>Há resposta da administração ainda não lida pelo autor.</summary>
    public bool HasUnreadReply =>
        Status is SupportStatus.Answered && (ReadByUserAt is null || ReadByUserAt < LastMessageAt);

    public static SupportThread Open(
        Guid videoId, Guid userId, string firstMessage, DateTimeOffset now, double? timestampSeconds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstMessage);

        var conversa = new SupportThread
        {
            Id = Guid.CreateVersion7(),
            VideoId = videoId,
            UserId = userId,
            Status = SupportStatus.Open,
            TimestampSeconds = timestampSeconds is > 0 ? timestampSeconds : null,
            CreatedAt = now,
            LastMessageAt = now
        };

        conversa._messages.Add(SupportMessage.Create(conversa.Id, userId, firstMessage, fromAdmin: false, now));

        return conversa;
    }

    /// <summary>
    /// Acrescenta uma mensagem. Quem escreve define para que lado a conversa volta: resposta
    /// da administração deixa a bola com o autor, e vice-versa.
    /// </summary>
    public SupportMessage Reply(Guid authorId, string body, bool fromAdmin, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        if (IsClosed)
            throw new InvalidOperationException("Esta conversa está encerrada.");

        if (!fromAdmin && authorId != UserId)
            throw new InvalidOperationException("Só o autor da conversa pode responder por ela.");

        var mensagem = SupportMessage.Create(Id, authorId, body, fromAdmin, now);

        _messages.Add(mensagem);
        LastMessageAt = now;
        Status = fromAdmin ? SupportStatus.Answered : SupportStatus.Open;

        // Quem escreve já leu o que estava lá; marcar aqui evita contar a própria mensagem
        // como pendente do outro lado.
        if (fromAdmin)
            ReadByAdminAt = now;
        else
            ReadByUserAt = now;

        return mensagem;
    }

    public void MarkReadByUser(DateTimeOffset now) => ReadByUserAt = now;

    public void MarkReadByAdmin(DateTimeOffset now) => ReadByAdminAt = now;

    public void Close(DateTimeOffset now)
    {
        Status = SupportStatus.Closed;
        ClosedAt = now;
    }

    /// <summary>Reabre uma conversa encerrada, devolvendo-a à fila da administração.</summary>
    public void Reopen(DateTimeOffset now)
    {
        Status = SupportStatus.Open;
        ClosedAt = null;
        LastMessageAt = now;
    }

    /// <summary>Verifica se alguém pode ler esta conversa.</summary>
    public bool IsVisibleTo(Guid? userId, bool isAdmin) => isAdmin || (userId is { } id && id == UserId);
}

/// <summary>Uma mensagem dentro de uma conversa de suporte.</summary>
public class SupportMessage
{
    /// <summary>Tamanho máximo de uma mensagem.</summary>
    public const int MaxLength = 4000;

    private SupportMessage() { }

    public Guid Id { get; private set; }
    public Guid ThreadId { get; private set; }
    public Guid AuthorId { get; private set; }
    public string Body { get; private set; } = string.Empty;

    /// <summary>Se veio da administração.</summary>
    public bool FromAdmin { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static SupportMessage Create(Guid threadId, Guid authorId, string body, bool fromAdmin, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var texto = body.Trim();

        return new SupportMessage
        {
            Id = Guid.CreateVersion7(),
            ThreadId = threadId,
            AuthorId = authorId,
            Body = texto.Length <= MaxLength ? texto : texto[..MaxLength],
            FromAdmin = fromAdmin,
            CreatedAt = now
        };
    }
}
