// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using OpenTube.Infrastructure.Security;

namespace OpenTube.Infrastructure.Tests.Security;

public class OneTimeCodeTests
{
    [Fact]
    public void O_codigo_tem_sempre_seis_digitos()
    {
        for (var i = 0; i < 500; i++)
        {
            var codigo = OneTimeCode.GenerateCode();

            Assert.Equal(OneTimeCode.Digits, codigo.Length);
            Assert.All(codigo, c => Assert.True(char.IsAsciiDigit(c)));
        }
    }

    [Fact]
    public void Os_codigos_nao_se_repetem_em_sequencia()
    {
        var codigos = Enumerable.Range(0, 200).Select(_ => OneTimeCode.GenerateCode()).ToList();

        // Com um milhão de valores possíveis, duzentos sorteios repetidos seriam sinal de
        // gerador quebrado, não de azar.
        Assert.True(codigos.Distinct().Count() > 190, "os códigos deveriam variar");
    }

    [Fact]
    public void O_token_e_seguro_para_usar_em_endereco_web()
    {
        var token = OneTimeCode.GenerateToken();

        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.True(token.Length >= 43, $"token curto demais: {token.Length}");
    }

    [Fact]
    public void Os_tokens_nao_se_repetem()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => OneTimeCode.GenerateToken()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void Recusa_token_curto_demais_para_ser_seguro()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OneTimeCode.GenerateToken(8));
    }
}
