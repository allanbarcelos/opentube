using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Email;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;

namespace OpenTube.Infrastructure.Security;

/// <summary>Por que um pedido de acesso não deu certo.</summary>
public enum AuthFailure
{
    None = 0,
    InvalidEmail = 1,
    RateLimited = 2,
    InvalidCode = 3,
    CodeExpired = 4,
    CodeAlreadyUsed = 5,
    TooManyAttempts = 6,
    UserDisabled = 7,
    NotInvited = 8
}

/// <summary>Resultado de um pedido de código.</summary>
/// <param name="Sent">Se o email foi despachado.</param>
/// <param name="Failure">Motivo, quando não foi.</param>
/// <param name="RetryAfter">Espera sugerida, quando barrado por limite.</param>
public readonly record struct CodeRequestResult(bool Sent, AuthFailure Failure, TimeSpan RetryAfter)
{
    public static CodeRequestResult Ok() => new(true, AuthFailure.None, TimeSpan.Zero);
}

/// <summary>Resultado de uma tentativa de entrada.</summary>
/// <param name="Succeeded">Se a pessoa entrou.</param>
/// <param name="Failure">Motivo, quando não entrou.</param>
/// <param name="Session">Sessão aberta, em caso de sucesso.</param>
/// <param name="User">Pessoa autenticada, em caso de sucesso.</param>
public sealed record SignInOutcome(bool Succeeded, AuthFailure Failure, AuthSession? Session, User? User)
{
    public static SignInOutcome Fail(AuthFailure failure) => new(false, failure, null, null);

    public static SignInOutcome Ok(AuthSession session, User user) => new(true, AuthFailure.None, session, user);
}

/// <summary>
/// Acesso sem senha. O sistema envia um código de seis dígitos e um link de uso único; nenhuma
/// senha é gerada, trafegada ou guardada em lugar nenhum.
/// </summary>
public class PasswordlessAuthService(
    OpenTubeDbContext db,
    IEmailSender email,
    IAuthRateLimiter rateLimiter,
    PrivacyHasher privacy,
    IOptions<SecurityOptions> options,
    TimeProvider clock,
    ILogger<PasswordlessAuthService> logger)
{
    private readonly SecurityOptions _options = options.Value;

    /// <summary>
    /// Emite e envia um código de acesso. Quando <paramref name="requireExistingUser"/> está
    /// ligado, um endereço desconhecido recebe a mesma resposta de sucesso sem receber email:
    /// dizer "este email não existe" entregaria a lista de convidados a quem perguntasse.
    /// </summary>
    public async Task<CodeRequestResult> RequestCodeAsync(
        string emailInput,
        AuthPurpose purpose = AuthPurpose.Login,
        string? ip = null,
        bool requireExistingUser = true,
        Guid? grantId = null,
        CancellationToken cancellationToken = default)
    {
        if (!EmailAddress.TryParse(emailInput, out var endereco))
            return new CodeRequestResult(false, AuthFailure.InvalidEmail, TimeSpan.Zero);

        var ipHash = privacy.HashIp(ip);

        var limite = await rateLimiter.CheckAsync(endereco, ipHash, cancellationToken);
        if (!limite.Allowed)
        {
            logger.LogWarning("Pedido de código barrado pelo limite de {Escopo}", limite.Scope);
            return new CodeRequestResult(false, AuthFailure.RateLimited, limite.RetryAfter);
        }

        await rateLimiter.RecordAsync(endereco, ipHash, cancellationToken);

        var usuario = await db.Users.FirstOrDefaultAsync(u => u.Email == endereco.Value, cancellationToken);

        if (usuario is not null && !usuario.IsActive)
            return new CodeRequestResult(false, AuthFailure.UserDisabled, TimeSpan.Zero);

        if (usuario is null && requireExistingUser)
        {
            logger.LogInformation("Pedido de código para endereço sem acesso; nenhum email enviado");
            return CodeRequestResult.Ok();
        }

        var agora = clock.GetUtcNow();
        var codigo = OneTimeCode.GenerateCode();
        var token = OneTimeCode.GenerateToken();
        var validade = purpose is AuthPurpose.Invite ? _options.InviteLifetime : _options.CodeLifetime;

        db.LoginCodes.Add(LoginCode.Issue(
            endereco,
            purpose,
            TokenHasher.Hash(codigo, _options.TokenPepper),
            TokenHasher.Hash(token, _options.TokenPepper),
            agora,
            validade,
            ipHash,
            grantId));

        await db.SaveChangesAsync(cancellationToken);

        var link = $"{_options.PublicUrl.TrimEnd('/')}/entrar/{token}";
        await email.SendAsync(EmailTemplates.AccessCode(endereco.Value, codigo, link, purpose, validade), cancellationToken);

        return CodeRequestResult.Ok();
    }

    /// <summary>Código e link recém-emitidos, entregues a quem vai montar o email.</summary>
    /// <param name="Code">Código de seis dígitos.</param>
    /// <param name="Link">Endereço de entrada direta.</param>
    /// <param name="Validity">Por quanto tempo valem.</param>
    public readonly record struct IssuedAccess(string Code, string Link, TimeSpan Validity);

    /// <summary>
    /// Emite um acesso vinculado a uma concessão, sem enviar email: quem convida monta a
    /// própria mensagem, dizendo o que foi liberado. Não passa pelo limitador de taxa porque
    /// o pedido vem de um administrador já autenticado, e não de um desconhecido tentando
    /// adivinhar códigos.
    /// </summary>
    public async Task<IssuedAccess> IssueInviteAsync(
        EmailAddress email,
        Guid grantId,
        CancellationToken cancellationToken = default)
    {
        var agora = clock.GetUtcNow();
        var codigo = OneTimeCode.GenerateCode();
        var token = OneTimeCode.GenerateToken();

        db.LoginCodes.Add(LoginCode.Issue(
            email,
            AuthPurpose.Invite,
            TokenHasher.Hash(codigo, _options.TokenPepper),
            TokenHasher.Hash(token, _options.TokenPepper),
            agora,
            _options.InviteLifetime,
            grantId: grantId));

        await db.SaveChangesAsync(cancellationToken);

        return new IssuedAccess(codigo, $"{_options.PublicUrl.TrimEnd('/')}/entrar/{token}", _options.InviteLifetime);
    }

    /// <summary>Confere o código de seis dígitos e abre a sessão.</summary>
    public async Task<SignInOutcome> VerifyCodeAsync(
        string emailInput,
        string code,
        string? ip = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        if (!EmailAddress.TryParse(emailInput, out var endereco))
            return SignInOutcome.Fail(AuthFailure.InvalidEmail);

        var agora = clock.GetUtcNow();
        var digitado = (code ?? string.Empty).Trim();

        var candidato = await db.LoginCodes
            .Where(c => c.Email == endereco.Value && c.ConsumedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (candidato is null)
            return SignInOutcome.Fail(AuthFailure.InvalidCode);

        if (candidato.IsExhausted)
            return SignInOutcome.Fail(AuthFailure.TooManyAttempts);

        if (candidato.IsExpiredAt(agora))
            return SignInOutcome.Fail(AuthFailure.CodeExpired);

        if (!TokenHasher.Verify(digitado, candidato.CodeHash, _options.TokenPepper))
        {
            candidato.RegisterFailedAttempt();
            await db.SaveChangesAsync(cancellationToken);

            return SignInOutcome.Fail(candidato.IsExhausted ? AuthFailure.TooManyAttempts : AuthFailure.InvalidCode);
        }

        return await ConcluirAsync(candidato, endereco, ip, userAgent, cancellationToken);
    }

    /// <summary>Confere o token do link de acesso direto e abre a sessão.</summary>
    public async Task<SignInOutcome> VerifyTokenAsync(
        string token,
        string? ip = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return SignInOutcome.Fail(AuthFailure.InvalidCode);

        var hash = TokenHasher.Hash(token.Trim(), _options.TokenPepper);

        var candidato = await db.LoginCodes.FirstOrDefaultAsync(c => c.TokenHash == hash, cancellationToken);

        if (candidato is null)
            return SignInOutcome.Fail(AuthFailure.InvalidCode);

        if (candidato.IsConsumed)
            return SignInOutcome.Fail(AuthFailure.CodeAlreadyUsed);

        if (candidato.IsExpiredAt(clock.GetUtcNow()))
            return SignInOutcome.Fail(AuthFailure.CodeExpired);

        return await ConcluirAsync(candidato, EmailAddress.Parse(candidato.Email), ip, userAgent, cancellationToken);
    }

    /// <summary>
    /// Recupera a sessão válida e a renova quando já passou da metade da validade. Renovar a
    /// cada requisição faria uma escrita no banco por página carregada, sem ganho nenhum.
    /// </summary>
    public async Task<(AuthSession Session, User User)?> TouchSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var agora = clock.GetUtcNow();

        var sessao = await db.AuthSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (sessao is null || !sessao.IsValidAt(agora))
            return null;

        var usuario = await db.Users.FirstOrDefaultAsync(u => u.Id == sessao.UserId, cancellationToken);
        if (usuario is null || !usuario.IsActive)
            return null;

        if (sessao.ExpiresAt - agora < _options.SessionLifetime / 2)
        {
            sessao.Touch(agora, _options.SessionLifetime);
            usuario.Touch(agora);
            await db.SaveChangesAsync(cancellationToken);
        }

        return (sessao, usuario);
    }

    public async Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var sessao = await db.AuthSessions.FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (sessao is null)
            return;

        sessao.Revoke(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Encerra todas as sessões de uma pessoa — usado ao revogar acesso.</summary>
    public async Task<int> RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var agora = clock.GetUtcNow();

        var encerradas = await db.AuthSessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, agora), cancellationToken);

        // A atualização em lote não passa pelo rastreador: sem descartar o que já estava
        // carregado, uma sessão encerrada continuaria parecendo válida neste contexto.
        foreach (var rastreada in db.ChangeTracker.Entries<AuthSession>()
                     .Where(e => e.Entity.UserId == userId)
                     .ToList())
        {
            rastreada.State = EntityState.Detached;
        }

        return encerradas;
    }

    private async Task<SignInOutcome> ConcluirAsync(
        LoginCode codigo,
        EmailAddress endereco,
        string? ip,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var agora = clock.GetUtcNow();

        var usuario = await db.Users.FirstOrDefaultAsync(u => u.Email == endereco.Value, cancellationToken);

        if (usuario is null)
        {
            // O convidado vira usuário na primeira entrada: até aqui ele só existia
            // como destinatário de uma concessão.
            usuario = User.Create(endereco, agora);
            db.Users.Add(usuario);
        }
        else if (!usuario.IsActive)
        {
            return SignInOutcome.Fail(AuthFailure.UserDisabled);
        }

        codigo.Consume(agora);
        usuario.Touch(agora);

        var sessao = AuthSession.Open(usuario.Id, agora, _options.SessionLifetime, privacy.HashIp(ip), userAgent);
        db.AuthSessions.Add(sessao);

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Sessão aberta para {UsuarioId}", usuario.Id);

        return SignInOutcome.Ok(sessao, usuario);
    }
}
