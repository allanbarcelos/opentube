using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;

namespace OpenTube.Domain.Entities;

/// <summary>
/// Uma concessão de acesso. Os quatro tipos de liberação — público, pessoa, domínio e link
/// secreto — são a mesma entidade com sujeitos diferentes, e recaem sobre um vídeo, uma
/// coleção ou o acervo inteiro.
/// </summary>
public class AccessGrant
{
    private AccessGrant() { }

    public Guid Id { get; private set; }

    public GrantSubjectType SubjectType { get; private set; }

    /// <summary>
    /// Email, domínio ou resumo do token do link, conforme o tipo. Vazio para o acesso
    /// público, que não tem sujeito.
    /// </summary>
    public string SubjectValue { get; private set; } = string.Empty;

    public GrantTargetType TargetType { get; private set; }

    /// <summary>Vídeo ou coleção alvo; nulo quando a concessão vale para todo o acervo.</summary>
    public Guid? TargetId { get; private set; }

    /// <summary>A partir de quando vale. Nulo significa desde já.</summary>
    public DateTimeOffset? StartsAt { get; private set; }

    /// <summary>Até quando vale. Nulo significa acesso eterno.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>
    /// Prazo contado a partir do primeiro uso, em vez de uma data fixa. É o que permite
    /// conceder "trinta dias de acesso" sem saber quando a pessoa vai abrir o convite.
    /// </summary>
    public TimeSpan? DurationAfterFirstUse { get; private set; }

    public DateTimeOffset? FirstUsedAt { get; private set; }

    /// <summary>Teto de visualizações; nulo significa ilimitado.</summary>
    public int? MaxViews { get; private set; }

    public int ViewsUsed { get; private set; }

    public bool CanDownload { get; private set; }

    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Anotação livre do administrador, para lembrar o motivo da liberação.</summary>
    public string? Note { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsExhausted => MaxViews is { } teto && ViewsUsed >= teto;

    /// <summary>Data efetiva de término, considerando o prazo relativo ao primeiro uso.</summary>
    public DateTimeOffset? EffectiveExpiry
    {
        get
        {
            if (DurationAfterFirstUse is not { } prazo)
                return ExpiresAt;

            var relativo = FirstUsedAt is { } inicio ? inicio + prazo : (DateTimeOffset?)null;

            // Havendo as duas formas, vale a que terminar primeiro: o prazo relativo não
            // pode esticar um acesso que já tinha data de fim.
            return ExpiresAt is null || relativo is null
                ? relativo ?? ExpiresAt
                : relativo < ExpiresAt ? relativo : ExpiresAt;
        }
    }

    public static AccessGrant Create(
        GrantSubjectType subjectType,
        string? subjectValue,
        GrantTargetType targetType,
        Guid? targetId,
        Guid createdBy,
        DateTimeOffset now,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? expiresAt = null,
        TimeSpan? durationAfterFirstUse = null,
        int? maxViews = null,
        bool canDownload = false,
        string? note = null)
    {
        if (targetType is GrantTargetType.All && targetId is not null)
            throw new ArgumentException("A grant for the whole library does not point at a target.", nameof(targetId));

        if (targetType is not GrantTargetType.All && (targetId is null || targetId == Guid.Empty))
            throw new ArgumentException("Name the video or collection this grant applies to.", nameof(targetId));

        if (expiresAt is { } fim && startsAt is { } inicio && fim <= inicio)
            throw new ArgumentException("The end of the grant must be after the start.", nameof(expiresAt));

        if (durationAfterFirstUse is { } prazo && prazo <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(durationAfterFirstUse), "The period must be positive.");

        if (maxViews is { } teto && teto <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxViews), "The view limit must be positive.");

        return new AccessGrant
        {
            Id = Guid.CreateVersion7(),
            SubjectType = subjectType,
            SubjectValue = NormalizarSujeito(subjectType, subjectValue),
            TargetType = targetType,
            TargetId = targetId,
            StartsAt = startsAt,
            ExpiresAt = expiresAt,
            DurationAfterFirstUse = durationAfterFirstUse,
            MaxViews = maxViews,
            CanDownload = canDownload,
            CreatedBy = createdBy,
            CreatedAt = now,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };
    }

    /// <summary>Concessão para um endereço de email específico.</summary>
    public static AccessGrant ForUser(EmailAddress email, GrantTargetType targetType, Guid? targetId, Guid createdBy, DateTimeOffset now,
        DateTimeOffset? expiresAt = null, TimeSpan? durationAfterFirstUse = null, string? note = null) =>
        Create(GrantSubjectType.User, email.Value, targetType, targetId, createdBy, now,
            expiresAt: expiresAt, durationAfterFirstUse: durationAfterFirstUse, note: note);

    /// <summary>Concessão para qualquer email de um domínio.</summary>
    public static AccessGrant ForDomain(string domain, GrantTargetType targetType, Guid? targetId, Guid createdBy, DateTimeOffset now,
        DateTimeOffset? expiresAt = null, TimeSpan? durationAfterFirstUse = null, string? note = null) =>
        Create(GrantSubjectType.Domain, domain, targetType, targetId, createdBy, now,
            expiresAt: expiresAt, durationAfterFirstUse: durationAfterFirstUse, note: note);

    /// <summary>Concessão por link secreto. O token é guardado apenas como resumo.</summary>
    public static AccessGrant ForLink(string tokenHash, GrantTargetType targetType, Guid? targetId, Guid createdBy, DateTimeOffset now,
        DateTimeOffset? expiresAt = null, int? maxViews = null, string? note = null) =>
        Create(GrantSubjectType.Link, tokenHash, targetType, targetId, createdBy, now,
            expiresAt: expiresAt, maxViews: maxViews, note: note);

    /// <summary>Verifica se a concessão está valendo neste instante.</summary>
    public bool IsActiveAt(DateTimeOffset now) => IsActiveAt(now, ignoreViewLimit: false);

    /// <summary>
    /// Verifica se a concessão está valendo, podendo desconsiderar o teto de visualizações.
    /// A reprodução que consumiu a última visualização ainda precisa buscar as versões, os
    /// segmentos e as legendas; o teto barra uma reprodução nova, não a que já começou.
    /// Revogação e prazo continuam valendo sempre.
    /// </summary>
    public bool IsActiveAt(DateTimeOffset now, bool ignoreViewLimit)
    {
        if (IsRevoked || (IsExhausted && !ignoreViewLimit))
            return false;

        if (StartsAt is { } inicio && now < inicio)
            return false;

        return EffectiveExpiry is not { } fim || now < fim;
    }

    /// <summary>Verifica se o sujeito da concessão corresponde a quem está pedindo.</summary>
    public bool MatchesSubject(GrantSubjectType type, string? value) =>
        SubjectType == type && type switch
        {
            GrantSubjectType.Public => true,
            GrantSubjectType.Link => true,
            _ => !string.IsNullOrWhiteSpace(value) &&
                 string.Equals(SubjectValue, value.Trim().ToLowerInvariant(), StringComparison.Ordinal)
        };

    /// <summary>Verifica se a concessão alcança o vídeo informado.</summary>
    public bool Covers(Guid videoId, IReadOnlyCollection<Guid> collectionIds) => TargetType switch
    {
        GrantTargetType.All => true,
        GrantTargetType.Video => TargetId == videoId,
        GrantTargetType.Collection => TargetId is { } alvo && collectionIds.Contains(alvo),
        _ => false
    };

    /// <summary>
    /// Registra um uso. O primeiro uso dispara a contagem do prazo relativo, e é por isso
    /// que precisa ser gravado mesmo quando não há limite de visualizações.
    /// </summary>
    public void RegisterUse(DateTimeOffset now)
    {
        FirstUsedAt ??= now;
        ViewsUsed++;
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    public void Restore() => RevokedAt = null;

    /// <summary>Altera a validade de uma concessão já criada.</summary>
    public void Reschedule(DateTimeOffset? startsAt, DateTimeOffset? expiresAt, TimeSpan? durationAfterFirstUse)
    {
        if (expiresAt is { } fim && startsAt is { } inicio && fim <= inicio)
            throw new ArgumentException("The end of the grant must be after the start.", nameof(expiresAt));

        if (durationAfterFirstUse is { } prazo && prazo <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(durationAfterFirstUse), "The period must be positive.");

        StartsAt = startsAt;
        ExpiresAt = expiresAt;
        DurationAfterFirstUse = durationAfterFirstUse;
    }

    public void SetNote(string? note) => Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    public void AllowDownload(bool allowed) => CanDownload = allowed;

    private static string NormalizarSujeito(GrantSubjectType tipo, string? valor) => tipo switch
    {
        GrantSubjectType.Public => string.Empty,
        // O resumo do token é comparado byte a byte; baixar a caixa o corromperia.
        GrantSubjectType.Link => string.IsNullOrWhiteSpace(valor)
            ? throw new ArgumentException("A link grant needs the token digest.", nameof(valor))
            : valor.Trim(),
        _ => string.IsNullOrWhiteSpace(valor)
            ? throw new ArgumentException("The grant needs the person's email or the domain.", nameof(valor))
            : valor.Trim().ToLowerInvariant()
    };
}
