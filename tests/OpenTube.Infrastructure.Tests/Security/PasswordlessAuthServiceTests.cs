// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenTube.Domain.Entities;
using OpenTube.Domain.Enums;
using OpenTube.Domain.ValueObjects;
using OpenTube.Infrastructure.Options;
using OpenTube.Infrastructure.Persistence;
using OpenTube.Infrastructure.Security;
using OpenTube.Infrastructure.Tests.Support;
using OpenTube.TestSupport;

namespace OpenTube.Infrastructure.Tests.Security;

[Collection(IntegrationCollection.Name)]
public class PasswordlessAuthServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Convidado = "allan@barcelos.dev";
    private static readonly DateTimeOffset Agora = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _relogio = new(Agora);
    private readonly FakeEmailSender _email = new();

    private readonly SecurityOptions _seguranca = new()
    {
        TokenPepper = "segredo-de-teste",
        IpHashPepper = "segredo-de-ip",
        CodeLifetime = TimeSpan.FromMinutes(15),
        InviteLifetime = TimeSpan.FromDays(7),
        SessionLifetime = TimeSpan.FromDays(30),
        CodesPerWindow = 5,
        PublicUrl = "https://opentube.org"
    };

    public Task InitializeAsync() => postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private (PasswordlessAuthService Servico, OpenTubeDbContext Db) Criar()
    {
        var db = postgres.CreateContext();
        var opcoes = Microsoft.Extensions.Options.Options.Create(_seguranca);
        var privacidade = new PrivacyHasher(opcoes);
        var limitador = new AuthRateLimiter(db, opcoes, _relogio);

        return (new PasswordlessAuthService(
            db, _email, limitador, privacidade, opcoes, _relogio, NullLogger<PasswordlessAuthService>.Instance), db);
    }

    private async Task<User> CriarUsuarioAsync(string email = Convidado, bool admin = false)
    {
        await using var db = postgres.CreateContext();
        var usuario = User.Create(EmailAddress.Parse(email), Agora, admin);
        db.Users.Add(usuario);
        await db.SaveChangesAsync();

        return usuario;
    }

    [Fact]
    public async Task Envia_codigo_e_link_para_quem_ja_tem_acesso()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        var resultado = await servico.RequestCodeAsync(Convidado, ip: "203.0.113.5");

        Assert.True(resultado.Sent);
        Assert.Single(_email.Sent);
        Assert.Equal(Convidado, _email.Last!.To);
        Assert.Equal(6, _email.LastCode().Length);
        Assert.StartsWith("https://opentube.org/sign-in/", _email.Last.TextBody[_email.Last.TextBody.IndexOf("https://", StringComparison.Ordinal)..].Split('\n')[0]);
    }

    [Fact]
    public async Task Nao_revela_se_o_endereco_tem_acesso()
    {
        var (servico, db) = Criar();
        await using var _ = db;

        var resultado = await servico.RequestCodeAsync("desconhecido@barcelos.dev");

        // A resposta é de sucesso, mas nenhum email sai: dizer "não existe" entregaria
        // a lista de convidados a quem perguntasse.
        Assert.True(resultado.Sent);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Cria_o_usuario_na_primeira_entrada_quando_o_convite_dispensa_cadastro()
    {
        var (servico, db) = Criar();
        await using var _ = db;

        await servico.RequestCodeAsync("novo@barcelos.dev", AuthPurpose.Invite, requireExistingUser: false);
        var entrada = await servico.VerifyCodeAsync("novo@barcelos.dev", _email.LastCode());

        Assert.True(entrada.Succeeded);
        Assert.Equal("novo@barcelos.dev", entrada.User!.Email);
        Assert.False(entrada.User.IsAdmin);
    }

    [Fact]
    public async Task Quem_foi_convidado_consegue_pedir_um_codigo_novo()
    {
        // O convidado só vira usuário na primeira entrada; até lá, é a concessão que prova
        // que ele tem acesso e pode pedir outro código se perder o email do convite.
        await using (var db = postgres.CreateContext())
        {
            db.AccessGrants.Add(AccessGrant.ForUser(
                EmailAddress.Parse(Convidado), GrantTargetType.All, null, Guid.CreateVersion7(), Agora));
            await db.SaveChangesAsync();
        }

        var (servico, db2) = Criar();
        await using var _ = db2;

        var resultado = await servico.RequestCodeAsync(Convidado);

        Assert.True(resultado.Sent);
        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task Concessao_por_dominio_tambem_permite_pedir_codigo()
    {
        await using (var db = postgres.CreateContext())
        {
            db.AccessGrants.Add(AccessGrant.ForDomain(
                "barcelos.dev", GrantTargetType.All, null, Guid.CreateVersion7(), Agora));
            await db.SaveChangesAsync();
        }

        var (servico, db2) = Criar();
        await using var _ = db2;

        Assert.True((await servico.RequestCodeAsync("qualquer@barcelos.dev")).Sent);
        Assert.Single(_email.Sent);
    }

    [Fact]
    public async Task Concessao_revogada_nao_permite_pedir_codigo()
    {
        await using (var db = postgres.CreateContext())
        {
            var concessao = AccessGrant.ForUser(
                EmailAddress.Parse(Convidado), GrantTargetType.All, null, Guid.CreateVersion7(), Agora);
            concessao.Revoke(Agora);
            db.AccessGrants.Add(concessao);
            await db.SaveChangesAsync();
        }

        var (servico, db2) = Criar();
        await using var _ = db2;

        var resultado = await servico.RequestCodeAsync(Convidado);

        Assert.True(resultado.Sent);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Recusa_endereco_malformado()
    {
        var (servico, db) = Criar();
        await using var _ = db;

        var resultado = await servico.RequestCodeAsync("nao-e-email");

        Assert.False(resultado.Sent);
        Assert.Equal(AuthFailure.InvalidEmail, resultado.Failure);
    }

    [Fact]
    public async Task Recusa_usuario_desativado()
    {
        var usuario = await CriarUsuarioAsync();
        await using (var db = postgres.CreateContext())
        {
            var alvo = await db.Users.SingleAsync(u => u.Id == usuario.Id);
            alvo.Disable(Agora);
            await db.SaveChangesAsync();
        }

        var (servico, db2) = Criar();
        await using var _ = db2;

        var resultado = await servico.RequestCodeAsync(Convidado);

        Assert.False(resultado.Sent);
        Assert.Equal(AuthFailure.UserDisabled, resultado.Failure);
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Entra_com_o_codigo_correto_e_abre_sessao()
    {
        var usuario = await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync(Convidado);

        var entrada = await servico.VerifyCodeAsync(Convidado, _email.LastCode(), "203.0.113.5", "Mozilla/5.0");

        Assert.True(entrada.Succeeded);
        Assert.Equal(usuario.Id, entrada.User!.Id);
        Assert.Equal(Agora.Add(_seguranca.SessionLifetime), entrada.Session!.ExpiresAt);
        Assert.Equal("Mozilla/5.0", entrada.Session.UserAgent);
    }

    [Fact]
    public async Task O_endereco_de_origem_nao_e_guardado_em_texto_claro()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync(Convidado, ip: "203.0.113.5");

        var entrada = await servico.VerifyCodeAsync(Convidado, _email.LastCode(), "203.0.113.5");

        Assert.NotNull(entrada.Session!.IpHash);
        Assert.DoesNotContain("203.0.113.5", entrada.Session.IpHash);
    }

    [Fact]
    public async Task Recusa_codigo_errado()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync(Convidado);

        var entrada = await servico.VerifyCodeAsync(Convidado, "000000");

        Assert.False(entrada.Succeeded);
        Assert.Equal(AuthFailure.InvalidCode, entrada.Failure);
    }

    [Fact]
    public async Task Queima_o_codigo_depois_de_cinco_erros()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync(Convidado);
        var correto = _email.LastCode();

        for (var tentativa = 1; tentativa <= LoginCode.MaxAttempts; tentativa++)
            await servico.VerifyCodeAsync(Convidado, "000000");

        var comCodigoCerto = await servico.VerifyCodeAsync(Convidado, correto);

        Assert.False(comCodigoCerto.Succeeded);
        Assert.Equal(AuthFailure.TooManyAttempts, comCodigoCerto.Failure);
    }

    /// <summary>Pede um código e erra as cinco vezes que ele aceita.</summary>
    private async Task QueimarUmCodigoAsync(PasswordlessAuthService servico)
    {
        await servico.RequestCodeAsync(Convidado);
        var errado = _email.LastCode() == "000000" ? "111111" : "000000";

        for (var tentativa = 1; tentativa <= LoginCode.MaxAttempts; tentativa++)
            await servico.VerifyCodeAsync(Convidado, errado);
    }

    [Fact]
    public async Task Trocar_de_codigo_nao_renova_os_palpites_alem_do_teto_do_dia()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        // Vinte erros: quatro códigos queimados, todos dentro da janela de pedidos.
        for (var i = 0; i < 4; i++)
            await QueimarUmCodigoAsync(servico);

        await servico.RequestCodeAsync(Convidado);
        var comCodigoCerto = await servico.VerifyCodeAsync(Convidado, _email.LastCode());

        Assert.False(comCodigoCerto.Succeeded);
        Assert.Equal(AuthFailure.LockedOut, comCodigoCerto.Failure);
    }

    [Fact]
    public async Task Com_a_digitacao_travada_o_link_do_email_ainda_entra()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        for (var i = 0; i < 4; i++)
            await QueimarUmCodigoAsync(servico);

        await servico.RequestCodeAsync(Convidado);
        var entrada = await servico.VerifyTokenAsync(_email.LastToken());

        Assert.True(entrada.Succeeded);
    }

    [Fact]
    public async Task O_teto_do_dia_se_solta_depois_de_24_horas()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        for (var i = 0; i < 4; i++)
            await QueimarUmCodigoAsync(servico);

        _relogio.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        await servico.RequestCodeAsync(Convidado);

        Assert.True((await servico.VerifyCodeAsync(Convidado, _email.LastCode())).Succeeded);
    }

    [Fact]
    public async Task Palpites_em_paralelo_nao_passam_do_limite_de_tentativas()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync(Convidado);
        var correto = _email.LastCode();
        var errado = correto == "000000" ? "111111" : "000000";

        // Cada palpite num contexto próprio, como requisições distintas chegando juntas.
        var palpites = Enumerable.Range(0, 30).Select(async _ =>
        {
            var (paralelo, contexto) = Criar();
            await using var __ = contexto;

            return await paralelo.VerifyCodeAsync(Convidado, errado);
        });

        await Task.WhenAll(palpites);

        await using var leitura = postgres.CreateContext();
        var codigo = await leitura.LoginCodes.SingleAsync();

        Assert.Equal(LoginCode.MaxAttempts, codigo.Attempts);
        Assert.Equal(AuthFailure.TooManyAttempts, (await servico.VerifyCodeAsync(Convidado, correto)).Failure);
    }

    [Fact]
    public async Task Link_usado_ao_mesmo_tempo_abre_uma_unica_sessao()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync(Convidado);
        var token = _email.LastToken();

        var entradas = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            var (paralelo, contexto) = Criar();
            await using var __ = contexto;

            return await paralelo.VerifyTokenAsync(token);
        }));

        Assert.Single(entradas, e => e.Succeeded);

        await using var leitura = postgres.CreateContext();
        Assert.Equal(1, await leitura.AuthSessions.CountAsync());
    }

    [Fact]
    public async Task O_codigo_expira()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync(Convidado);
        var codigo = _email.LastCode();

        _relogio.Advance(_seguranca.CodeLifetime + TimeSpan.FromMinutes(1));

        var entrada = await servico.VerifyCodeAsync(Convidado, codigo);

        Assert.False(entrada.Succeeded);
        Assert.Equal(AuthFailure.CodeExpired, entrada.Failure);
    }

    [Fact]
    public async Task O_codigo_so_vale_uma_vez()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync(Convidado);
        var codigo = _email.LastCode();

        Assert.True((await servico.VerifyCodeAsync(Convidado, codigo)).Succeeded);

        var segunda = await servico.VerifyCodeAsync(Convidado, codigo);

        Assert.False(segunda.Succeeded);
        Assert.Equal(AuthFailure.InvalidCode, segunda.Failure);
    }

    [Fact]
    public async Task O_codigo_de_um_endereco_nao_serve_para_outro()
    {
        await CriarUsuarioAsync();
        await CriarUsuarioAsync("outro@barcelos.dev");
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync(Convidado);
        var codigoDoAllan = _email.LastCode();

        await servico.RequestCodeAsync("outro@barcelos.dev");

        var entrada = await servico.VerifyCodeAsync("outro@barcelos.dev", codigoDoAllan);

        Assert.False(entrada.Succeeded);
    }

    [Fact]
    public async Task Entra_pelo_link_de_uso_unico()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync(Convidado);

        var entrada = await servico.VerifyTokenAsync(_email.LastToken());

        Assert.True(entrada.Succeeded);
        Assert.Equal(Convidado, entrada.User!.Email);
    }

    [Fact]
    public async Task O_link_so_funciona_uma_vez()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync(Convidado);
        var token = _email.LastToken();

        await servico.VerifyTokenAsync(token);
        var segunda = await servico.VerifyTokenAsync(token);

        Assert.False(segunda.Succeeded);
        Assert.Equal(AuthFailure.CodeAlreadyUsed, segunda.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("token-inventado")]
    public async Task Recusa_link_invalido(string token)
    {
        var (servico, db) = Criar();
        await using var _ = db;

        var entrada = await servico.VerifyTokenAsync(token);

        Assert.False(entrada.Succeeded);
        Assert.Equal(AuthFailure.InvalidCode, entrada.Failure);
    }

    [Fact]
    public async Task O_convite_dura_mais_que_o_codigo_comum()
    {
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync("novo@barcelos.dev", AuthPurpose.Invite, requireExistingUser: false);
        var token = _email.LastToken();

        _relogio.Advance(TimeSpan.FromDays(6));

        Assert.True((await servico.VerifyTokenAsync(token)).Succeeded);
    }

    [Fact]
    public async Task Renova_a_sessao_a_cada_acesso()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync(Convidado);
        var entrada = await servico.VerifyCodeAsync(Convidado, _email.LastCode());

        _relogio.Advance(TimeSpan.FromDays(20));
        var renovada = await servico.TouchSessionAsync(entrada.Session!.Id);

        Assert.NotNull(renovada);
        Assert.Equal(_relogio.GetUtcNow().Add(_seguranca.SessionLifetime), renovada.Value.Session.ExpiresAt);
    }

    [Fact]
    public async Task A_sessao_expira_sem_uso()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync(Convidado);
        var entrada = await servico.VerifyCodeAsync(Convidado, _email.LastCode());

        _relogio.Advance(_seguranca.SessionLifetime + TimeSpan.FromDays(1));

        Assert.Null(await servico.TouchSessionAsync(entrada.Session!.Id));
    }

    [Fact]
    public async Task Encerrar_a_sessao_corta_o_acesso_na_hora()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync(Convidado);
        var entrada = await servico.VerifyCodeAsync(Convidado, _email.LastCode());

        await servico.RevokeSessionAsync(entrada.Session!.Id);

        Assert.Null(await servico.TouchSessionAsync(entrada.Session.Id));
    }

    [Fact]
    public async Task Encerra_todas_as_sessoes_de_uma_pessoa()
    {
        var usuario = await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        var sessoes = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            await servico.RequestCodeAsync(Convidado);
            var entrada = await servico.VerifyCodeAsync(Convidado, _email.LastCode());
            sessoes.Add(entrada.Session!.Id);
        }

        var encerradas = await servico.RevokeAllSessionsAsync(usuario.Id);

        Assert.Equal(3, encerradas);
        foreach (var id in sessoes)
            Assert.Null(await servico.TouchSessionAsync(id));
    }

    [Fact]
    public async Task Desativar_a_pessoa_invalida_a_sessao_existente()
    {
        var usuario = await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;
        await servico.RequestCodeAsync(Convidado);
        var entrada = await servico.VerifyCodeAsync(Convidado, _email.LastCode());

        await using (var outro = postgres.CreateContext())
        {
            var alvo = await outro.Users.SingleAsync(u => u.Id == usuario.Id);
            alvo.Disable(Agora);
            await outro.SaveChangesAsync();
        }

        await using var db3 = postgres.CreateContext();
        var (servico2, _) = (new PasswordlessAuthService(
            db3, _email,
            new AuthRateLimiter(db3, Microsoft.Extensions.Options.Options.Create(_seguranca), _relogio),
            new PrivacyHasher(Microsoft.Extensions.Options.Options.Create(_seguranca)),
            Microsoft.Extensions.Options.Options.Create(_seguranca), _relogio,
            NullLogger<PasswordlessAuthService>.Instance), 0);

        Assert.Null(await servico2.TouchSessionAsync(entrada.Session!.Id));
    }

    [Fact]
    public async Task Barra_depois_de_varios_pedidos_do_mesmo_email()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        for (var i = 0; i < _seguranca.CodesPerWindow; i++)
            Assert.True((await servico.RequestCodeAsync(Convidado, ip: $"203.0.113.{i}")).Sent);

        var barrado = await servico.RequestCodeAsync(Convidado, ip: "198.51.100.1");

        Assert.False(barrado.Sent);
        Assert.Equal(AuthFailure.RateLimited, barrado.Failure);
        Assert.InRange(barrado.RetryAfter, TimeSpan.FromSeconds(1), AuthRateLimiter.Janela);
        Assert.Equal(_seguranca.CodesPerWindow, _email.Sent.Count);
    }

    [Fact]
    public async Task O_mesmo_email_volta_a_receber_codigo_depois_de_dez_minutos()
    {
        await CriarUsuarioAsync();
        var (servico, db) = Criar();
        await using var _ = db;

        for (var i = 0; i < _seguranca.CodesPerWindow; i++)
            await servico.RequestCodeAsync(Convidado, ip: "203.0.113.5");

        _relogio.Advance(AuthRateLimiter.Janela + TimeSpan.FromSeconds(1));

        Assert.True((await servico.RequestCodeAsync(Convidado, ip: "198.51.100.9")).Sent);
    }
}
