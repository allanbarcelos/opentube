# Imagem da aplicação. A restauração vem antes do código para que uma alteração de fonte não
# invalide o cache das dependências.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props OpenTube.sln ./
COPY src/OpenTube.Shared/OpenTube.Shared.csproj src/OpenTube.Shared/
COPY src/OpenTube.Domain/OpenTube.Domain.csproj src/OpenTube.Domain/
COPY src/OpenTube.Infrastructure/OpenTube.Infrastructure.csproj src/OpenTube.Infrastructure/
COPY src/OpenTube.Web/OpenTube.Web.csproj src/OpenTube.Web/

RUN dotnet restore src/OpenTube.Web/OpenTube.Web.csproj

COPY src/ src/
RUN dotnet publish src/OpenTube.Web/OpenTube.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Não roda como administrador: se algo der errado no processo, o estrago fica contido.
RUN useradd --uid 1001 --create-home opentube
USER 1001

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "OpenTube.Web.dll"]
