// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

using System.Net.Security;
using OpenTube.Infrastructure.Email;

namespace OpenTube.Infrastructure.Tests.Email;

public class SmtpEmailSenderTests
{
    [Fact]
    public void Certificado_valido_e_aceito_com_ou_sem_a_opcao()
    {
        Assert.True(SmtpEmailSender.AcceptsCertificate(SslPolicyErrors.None, acceptSelfSigned: false));
        Assert.True(SmtpEmailSender.AcceptsCertificate(SslPolicyErrors.None, acceptSelfSigned: true));
    }

    [Fact]
    public void Autoassinado_so_passa_com_a_opcao_ligada()
    {
        Assert.False(SmtpEmailSender.AcceptsCertificate(SslPolicyErrors.RemoteCertificateChainErrors, acceptSelfSigned: false));
        Assert.True(SmtpEmailSender.AcceptsCertificate(SslPolicyErrors.RemoteCertificateChainErrors, acceptSelfSigned: true));
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)]
    public void Nome_errado_ou_sem_certificado_e_recusado_mesmo_com_a_opcao(SslPolicyErrors erros)
    {
        Assert.False(SmtpEmailSender.AcceptsCertificate(erros, acceptSelfSigned: true));
    }
}
