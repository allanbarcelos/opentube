# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Allan Barcelos. OpenTube: https://github.com/allanbarcelos/opentube

.PHONY: help up up-d down restart logs ps build clean shell _docker \
        watch watch-web watch-worker deps-up deps-down test whisper headers

.DEFAULT_GOAL := help

COMPOSE_DEV := docker compose -f docker-compose.yml -f docker-compose.dev.yml
DEV_DEPS    := postgres minio mailpit

# ── Help ───────────────────────────────────────────────────────────────────────
help: ## Mostra este menu
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) \
		| awk 'BEGIN {FS = ":.*?## "}; {printf "\033[36m%-16s\033[0m %s\n", $$1, $$2}'

# ── Docker runtime (Colima / Docker Desktop) ───────────────────────────────────
# Not shown in help — internal prerequisite for up / up-d.
_docker:
	@docker info >/dev/null 2>&1 || { \
	  if command -v colima >/dev/null 2>&1; then \
	    echo "  Docker não está rodando. Iniciando Colima..."; \
	    colima start; \
	  else \
	    echo "  Docker daemon não está rodando."; \
	    echo "  Inicie o Docker Desktop ou o Colima antes de continuar."; \
	    exit 1; \
	  fi; }

# ── Start ──────────────────────────────────────────────────────────────────────
up: .env _docker ## Build e sobe todos os serviços (foreground)
	docker compose up --build

up-d: .env _docker ## Build e sobe todos os serviços (background)
	docker compose up --build -d

# ── Dev local com hot-reload ───────────────────────────────────────────────────
# Só as dependências rodam em container. A aplicação e o worker rodam no host
# com as credenciais do .env — nunca com usuário ou senha fixos no código.

deps-up: .env _docker ## Sobe só banco, storage e Mailpit, com portas no host
	$(COMPOSE_DEV) up -d $(DEV_DEPS)

deps-down: ## Para as dependências de dev
	$(COMPOSE_DEV) stop $(DEV_DEPS)

watch-web: .env ## Só a aplicação local em hot-reload (porta 5080)
	@set -a; . ./scripts/dev-env.sh; set +a; \
	  dotnet watch run --project src/OpenTube.Web

watch-worker: .env ## Só o worker local em hot-reload
	@set -a; . ./scripts/dev-env.sh; set +a; \
	  dotnet watch run --project src/OpenTube.Worker

watch: .env _docker deps-up ## Dev completo: deps em container + aplicação e worker no host
	@echo ""
	@echo "  Aplicação →  http://localhost:5080   (dotnet watch)"
	@echo "  Worker    →  processo local"
	@echo "  Banco     →  localhost:5432   usuário e senha no .env"
	@echo "  MinIO     →  http://localhost:9000   console :9001   credenciais no .env"
	@echo "  Mailpit   →  http://localhost:8025"
	@if command -v whisper-cli >/dev/null 2>&1 && [ -f .whisper/modelo.bin ]; then \
	  echo "  Legendas  →  transcrição com Whisper, modelo $$(readlink .whisper/modelo.bin)"; \
	else \
	  echo "  Legendas  →  transcrição desligada ('make whisper' instala o Whisper e o modelo)"; \
	fi
	@echo "  Ctrl+C encerra aplicação e worker. As deps seguem ('make deps-down' para pará-las)."
	@echo ""
	@trap 'kill 0' INT TERM; \
	 $(MAKE) --no-print-directory watch-web & \
	 $(MAKE) --no-print-directory watch-worker & \
	 wait

# Instala o whisper.cpp e baixa o modelo usado pelo worker do 'make watch'. O modelo pode ser
# trocado: make whisper m=base (rápido) | small (melhor) | large-v3-turbo-q5_0 (o mais preciso).
whisper: ## Liga a transcrição de legendas no make watch  →  make whisper  |  make whisper m=base
	@bash scripts/whisper.sh $(m)

# ── Stop ───────────────────────────────────────────────────────────────────────
down: ## Para e remove os containers
	docker compose down

restart: ## Reinicia um serviço sem rebuild  →  make restart s=app
	docker compose restart $(s)

# ── Observability ──────────────────────────────────────────────────────────────
logs: ## Segue logs (todos ou um serviço)  →  make logs s=app
	docker compose logs -f $(s)

ps: ## Lista containers e status
	docker compose ps

# ── Build ─────────────────────────────────────────────────────────────────────
build: .env ## Reconstrói as imagens sem subir
	docker compose build

# ── Tests ─────────────────────────────────────────────────────────────────────
# Os testes de integração sobem Postgres e MinIO próprios (Testcontainers): precisam do
# Docker, mas não do .env nem das dependências do 'make watch'. Os que usam FFmpeg são
# pulados quando ele não está instalado.
#
# Compilam em .artifacts/test, e não no bin/obj dos projetos: com o 'make watch' aberto, os
# dois builds disputariam os mesmos arquivos, e um teste pegaria uma saída pela metade.
TEST_ARTIFACTS := .artifacts/test

test: _docker ## Roda os testes  →  make test  |  make test p=Web  (Domain, Infrastructure, Worker, Web)
ifeq ($(strip $(p)),)
	dotnet test OpenTube.slnx --artifacts-path $(TEST_ARTIFACTS)
else
	dotnet test tests/OpenTube.$(p).Tests --artifacts-path $(TEST_ARTIFACTS)
endif

# ── License headers ───────────────────────────────────────────────────────────
# Todo arquivo de código leva o cabeçalho SPDX com licença e autoria; o CI recusa o que faltar.
headers: ## Acrescenta o cabeçalho de licença e autoria onde faltar (o CI confere)
	@python3 scripts/license-headers.py

# ── Maintenance ───────────────────────────────────────────────────────────────
clean: ## Remove containers, volumes e orphans  (reset completo)
	docker compose down -v --remove-orphans

shell: ## Abre shell em um container em execução  →  make shell s=app
	docker compose exec $(s) sh

# ── Bootstrap ─────────────────────────────────────────────────────────────────
.env:
	@bash scripts/gen-env.sh
