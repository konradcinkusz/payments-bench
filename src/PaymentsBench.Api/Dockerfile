# PaymentsBench.Api image (P6: one container per service, multi-stage).
#
# Build context is the repository root:
#   docker build -f src/PaymentsBench.Api/Dockerfile -t payments-bench-api .
#
# Layer order: solution-wide props and project files first and restored, source after,
# so a code change does not invalidate the restore layer.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/PaymentsBench.Domain/PaymentsBench.Domain.csproj src/PaymentsBench.Domain/
COPY src/PaymentsBench.Application/PaymentsBench.Application.csproj src/PaymentsBench.Application/
COPY src/PaymentsBench.Infrastructure/PaymentsBench.Infrastructure.csproj src/PaymentsBench.Infrastructure/
COPY src/PaymentsBench.ServiceDefaults/PaymentsBench.ServiceDefaults.csproj src/PaymentsBench.ServiceDefaults/
COPY src/PaymentsBench.Api/PaymentsBench.Api.csproj src/PaymentsBench.Api/
RUN dotnet restore src/PaymentsBench.Api/PaymentsBench.Api.csproj
COPY src/ src/
# Scheme and calendar data (rule 7) is linked into the project output from scenarios/.
COPY scenarios/ scenarios/
# Warnings-as-errors is the CI gate (ci.yml); the floating sdk:10.0 tag can ship newer analyzers
# than global.json pins, and a newer analyzer must not make an image unbuildable.
RUN dotnet publish src/PaymentsBench.Api/PaymentsBench.Api.csproj --configuration Release --no-restore -p:TreatWarningsAsErrors=false --output /app/publish

# Runtime image major version equals the TFM major version (net10.0).
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
LABEL org.opencontainers.image.source=https://github.com/konradcinkusz/payments-bench

# The aspnet image has no curl and the HEALTHCHECK needs an HTTP client. curl is installed
# with --no-install-recommends and the apt lists are removed in the same layer; it runs as
# root here, before USER drops privileges.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID

# /health runs every registered check (readiness); /alive is liveness only.
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "PaymentsBench.Api.dll"]
