# Build stage runs natively on the build machine and cross-compiles for the target
# platform, so building the amd64 image Cloud Run needs works from an arm64 Mac too.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY HandbotAPI/HandbotAPI.csproj HandbotAPI/
RUN dotnet restore HandbotAPI/HandbotAPI.csproj -a $TARGETARCH

COPY HandbotAPI/ HandbotAPI/
RUN dotnet publish HandbotAPI/HandbotAPI.csproj -a $TARGETARCH -c Release -o /app/publish --no-restore /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Cloud Run sends traffic to 8080 by default; set it explicitly rather than relying on the image default.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app/publish .

# Run as the non-root user that ships with the .NET images.
USER $APP_UID

ENTRYPOINT ["dotnet", "HandbotAPI.dll"]
