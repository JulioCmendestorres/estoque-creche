# syntax=docker/dockerfile:1
# Multi-stage: o mesmo binário roda como web (padrão), --migrate e --healthcheck.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Estoque.Web/Estoque.Web.csproj src/Estoque.Web/
RUN dotnet restore src/Estoque.Web/Estoque.Web.csproj
COPY src/ src/
RUN dotnet publish src/Estoque.Web/Estoque.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DataProtection__KeysPath=/home/app/.aspnet/DataProtection-Keys \
    Migrator__Path=/app/db/migrations \
    DOTNET_gcServer=0
# Diretórios graváveis do volume de chaves pertencem ao usuário não-root da imagem
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys \
    && chown -R $APP_UID /home/app/.aspnet
COPY --from=build /app/publish .
COPY db ./db
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Estoque.Web.dll"]
