.PHONY: help up up-d down restart logs ps build clean shell _docker \
        watch watch-web watch-worker deps-up deps-down

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
	@echo "  Ctrl+C encerra aplicação e worker. As deps seguem ('make deps-down' para pará-las)."
	@echo ""
	@trap 'kill 0' INT TERM; \
	 $(MAKE) --no-print-directory watch-web & \
	 $(MAKE) --no-print-directory watch-worker & \
	 wait

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

# ── Maintenance ───────────────────────────────────────────────────────────────
clean: ## Remove containers, volumes e orphans  (reset completo)
	docker compose down -v --remove-orphans

shell: ## Abre shell em um container em execução  →  make shell s=app
	docker compose exec $(s) sh

# ── Bootstrap ─────────────────────────────────────────────────────────────────
.env:
	@bash scripts/gen-env.sh
