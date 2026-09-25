# OpenTube

Plataforma de vídeo privada — um "YouTube interno" onde todo conteúdo nasce privado e o acesso é
concedido explicitamente: liberado para todos, direcionado a pessoas específicas ou a um domínio
de email inteiro, com validade opcional e registro detalhado de quem assistiu o quê.

---

## Índice

- [Visão geral](#visão-geral)
- [Modelo de acesso](#modelo-de-acesso)
- [Arquitetura](#arquitetura)
- [Pipeline de vídeo](#pipeline-de-vídeo)
- [Analytics](#analytics)
- [Suporte por vídeo](#suporte-por-vídeo)
- [Stack](#stack)
- [Estrutura do repositório](#estrutura-do-repositório)
- [Como rodar](#como-rodar)
- [Testes](#testes)
- [Segurança e privacidade](#segurança-e-privacidade)
- [Roadmap](#roadmap)

---

## Visão geral

| Recurso | Descrição |
| --- | --- |
| Home | Lista de vídeos visíveis para quem está acessando, com busca |
| Upload | Envio direto do navegador para o storage, sem passar pelo servidor da aplicação |
| Visibilidade | Todo vídeo nasce **privado**; o administrador promove para público ou restrito |
| Convites | Email com link de acesso e código de 6 dígitos — sem senha |
| Domínios | Porta de entrada própria por domínio verificado por DNS |
| Validade | Acesso eterno, até uma data ou por um período após o primeiro uso |
| Analytics | Quem assistiu, quando, de onde, em qual dispositivo e quanto de cada vídeo |
| Suporte | Comentários privados por vídeo, visíveis apenas ao autor e ao administrador |

---

## Modelo de acesso

### Visibilidade do vídeo

| Estado | Quem vê |
| --- | --- |
| `Private` | Somente administradores. É o padrão de todo upload. |
| `Public` | Qualquer visitante do site, sem autenticação. |
| `Restricted` | Somente quem possui uma concessão de acesso válida. |

### Concessões (`AccessGrant`)

Os quatro tipos de acesso são a mesma entidade com sujeitos diferentes:

| Sujeito | Significado |
| --- | --- |
| `User` | Um email específico (`allan@barcelos.dev`) |
| `Domain` | Qualquer email de um domínio verificado (`barcelos.dev`) |
| `Link` | Quem possuir um token secreto de compartilhamento |
| `Public` | Qualquer visitante |

Cada concessão aponta para um **vídeo**, uma **coleção** ou **todo o acervo**, e carrega janela de
validade (`starts_at` / `expires_at`, nulo = eterno), limite opcional de visualizações, permissão de
download e registro de revogação.

A decisão fica concentrada numa única função `CanWatch(viewer, video)` — toda a aplicação (home,
busca, player, legenda, thumbnail, download) passa por ela. É o ponto do sistema com maior cobertura
de testes, porque um erro ali vaza conteúdo confidencial.

### Autenticação sem senha

Nenhuma senha é gerada, trafegada ou armazenada. O convite traz um **link de uso único** e um
**código de 6 dígitos** (para quando o cliente de email quebra o link). Ambos ficam no banco apenas
como hash, expiram em 15 minutos (código) e 7 dias (convite), e o primeiro uso cria uma sessão em
cookie de 30 dias, renovável e revogável de imediato pelo administrador.

### Acesso por domínio

1. O administrador cadastra `barcelos.dev` e recebe um token de verificação.
2. O responsável pelo domínio publica `TXT _opentube-verify.barcelos.dev = <token>`.
3. Verificado o registro, o sistema libera a porta de entrada `/d/barcelos.dev`.
4. Quem chega nessa página informa um email do domínio e recebe o código por email.

A verificação por DNS existe para impedir que alguém cadastre um domínio que não controla. O envio
e a validação de códigos são limitados por taxa (IP, email e domínio), única barreira contra força
bruta num código de 6 dígitos.

---

## Arquitetura

```
                         ┌──────────────┐
   navegador ───────────▶│    Caddy     │  TLS automático
                         └──────┬───────┘
                                │
                ┌───────────────┼────────────────┐
                ▼               ▼                ▼
        ┌──────────────┐  ┌───────────┐   ┌────────────┐
        │  OpenTube    │  │  MinIO    │   │  Mailpit   │
        │  .Web        │  │  (S3)     │   │  (dev)     │
        │  Blazor      │  └───────────┘   └────────────┘
        └──────┬───────┘        ▲
               │                │
               ▼                │
        ┌──────────────┐  ┌─────┴────────┐
        │  PostgreSQL  │◀─│  OpenTube    │
        │              │  │  .Worker     │  FFmpeg
        └──────────────┘  └──────────────┘
```

O envio do arquivo vai **direto do navegador para o MinIO** via URLs assinadas de multipart, sem
passar pela aplicação. O worker é o único componente que escala por CPU e por isso vive em container
separado desde o primeiro dia.

---

## Pipeline de vídeo

1. **Upload** — a aplicação cria o registro do vídeo em `Draft` e devolve URLs assinadas; o navegador
   envia os pedaços direto ao bucket `originals`; ao concluir, enfileira o job de transcodificação.
2. **Análise** — `ffprobe` extrai duração, resolução e codecs, e rejeita arquivo inválido cedo.
3. **Transcodificação** — FFmpeg gera um ladder adaptativo (360p a 1080p, nunca acima da resolução
   original) em CMAF/fMP4, segmentos de 4 s com keyframes alinhados entre as versões.
4. **Derivados** — thumbnail, folha de sprites para prévia na barra de progresso e, futuramente,
   legendas automáticas.
5. **Publicação** — estado `Ready`, vídeo disponível conforme sua visibilidade.

O original é preservado no bucket `originals` para permitir reprocessamento.

### Entrega autorizada

A playlist HLS é servida por um endpoint da aplicação que valida o acesso e devolve o manifesto com
URLs assinadas de curta duração. Quando o volume justificar, a mesma verificação passa a ser feita
por `forward_auth` no Caddy (ou `auth_request` no nginx), que autoriza cada segmento e deixa o
servidor web empurrar os bytes — a aplicação só decide, não transporta.

Sobre proteção de conteúdo, sem rodeios: sem DRM, quem tem acesso legítimo consegue baixar. O que
funciona na prática é token curto, limite de sessões simultâneas por usuário, marca d'água dinâmica
com o email de quem assiste e registro completo de acesso. O empacotamento em CMAF mantém a porta
aberta para adicionar DRM depois sem reescrever nada.

---

## Analytics

O player envia um heartbeat a cada 10 segundos com o intervalo assistido desde o anterior, usando
`sendBeacon` para sobreviver ao fechamento da aba. Guardar **intervalos** em vez de porcentagem é o
que permite responder "ele pulou esse trecho?" e desenhar a curva de retenção segundo a segundo.

Um job periódico funde os intervalos por sessão (união de faixas, sem contar re-exibição duas vezes)
e agrega em tabelas diárias, mantendo o painel instantâneo mesmo com milhões de eventos.

**Painéis:** por vídeo (retenção, conclusão, dispositivos, erros), por usuário (linha do tempo
completa), por domínio e por convite — este último respondendo "convidei 12, 7 abriram, 5
assistiram, 2 terminaram", que costuma ser a métrica que interessa de verdade. Exportação em CSV.

---

## Suporte por vídeo

Os comentários funcionam como atendimento: cada conversa pertence a um par (vídeo, usuário), pode
estar ancorada a um instante do vídeo e é visível apenas ao autor e aos administradores. Tem status
(`aberto`, `respondido`, `fechado`) e notificação por email nos dois sentidos.

---

## Stack

| Camada | Tecnologia |
| --- | --- |
| Aplicação | .NET 10, Blazor Web App (SSR + `InteractiveServer` na área administrativa) |
| Interface | Bootstrap 5.3 com a paleta padrão |
| Banco | PostgreSQL 17, EF Core para o domínio e Dapper para agregações |
| Storage | MinIO (API S3), buckets `originals` e `vod` |
| Mídia | FFmpeg em worker próprio, HLS/CMAF, player `hls.js` |
| Fila | Tabela de jobs no PostgreSQL com `FOR UPDATE SKIP LOCKED` |
| Email | Abstração `IEmailSender`; Mailpit em desenvolvimento |
| Busca | `tsvector` com dicionário português e `pg_trgm` |
| Proxy | Caddy com TLS automático |

---

## Estrutura do repositório

```
src/
  OpenTube.Shared/          contratos e DTOs compartilhados
  OpenTube.Domain/          entidades e regras de acesso (sem dependência de infraestrutura)
  OpenTube.Infrastructure/  EF Core, storage S3, email, verificação DNS, fila
  OpenTube.Web/             Blazor: home, busca, player e área administrativa
  OpenTube.Worker/          transcodificação, derivados e agregação de analytics
tests/
  OpenTube.Domain.Tests/
  OpenTube.Infrastructure.Tests/
  OpenTube.Worker.Tests/
  OpenTube.Web.Tests/
```

---

## Como rodar

**Requisitos:** Docker e .NET SDK 10.

```bash
# sobe PostgreSQL, MinIO e Mailpit
docker compose up -d

# aplica as migrações e inicia a aplicação
dotnet run --project src/OpenTube.Web
```

| Serviço | Endereço | Credenciais |
| --- | --- | --- |
| Aplicação | http://localhost:5080 | — |
| MinIO (console) | http://localhost:9001 | `opentube` / `opentube123` |
| Mailpit | http://localhost:8025 | — |
| PostgreSQL | `localhost:5432` | `opentube` / `opentube` |

> **Sobre a imagem do MinIO:** as imagens públicas do MinIO deixaram de ser distribuídas pelo Docker
> Hub e pelo quay.io. O `docker-compose.yml` usa a última versão comunitária publicada, suficiente
> para desenvolvimento. Em produção, use o registro oficial com credenciais ou troque por qualquer
> outro servidor compatível com S3 (SeaweedFS, Garage, Amazon S3): a aplicação conversa apenas pela
> API S3, atrás da interface `IVideoStorage`.

---

## Testes

```bash
dotnet test
```

Cada fase do roadmap só é considerada concluída com sua suíte verde. A lógica sensível vive em
classes puras (regras de acesso, fusão de intervalos, cálculo do ladder de transcodificação,
limitador de taxa), testável sem banco nem rede; o restante usa containers efêmeros.

---

## Segurança e privacidade

- Nenhuma senha é gerada ou enviada por email.
- Códigos e tokens ficam apenas como hash, com uso único e expiração curta.
- Limite de taxa no envio e na validação de códigos, com bloqueio progressivo.
- IP registrado como hash com segredo rotativo; guarda-se o país, não o endereço.
- Eventos brutos de reprodução retidos por 24 meses; agregados permanecem.
- Exclusão de usuário anonimiza os eventos em vez de apagá-los.
- Registro de auditoria de toda ação administrativa: conceder, revogar, publicar, excluir.
- Aviso explícito ao convidado, no primeiro acesso, de que a visualização é registrada.

---

## Roadmap

| Fase | Escopo | Estado |
| --- | --- | --- |
| 1 | Núcleo: upload, transcodificação, player, home e busca | **concluída** |
| 2 | Acesso: concessões, convites e coleções | **concluída** |
| 3 | Domínios: verificação por DNS e porta de entrada dedicada | **concluída** |
| 4 | Analytics: coleta, agregação, painéis e exportação | em andamento |
| 5 | Suporte: conversas privadas por vídeo | pendente |
| 6 | Refino: legendas automáticas, marca d'água, auditoria, autorização por segmento | pendente |

---

## Licença

Uso privado.
