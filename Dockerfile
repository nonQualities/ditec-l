# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first (better layer caching on rebuilds)
COPY DITEC.Attendance.csproj ./
RUN dotnet restore DITEC.Attendance.csproj

COPY . .
RUN dotnet publish DITEC.Attendance.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
# Render sets $PORT at runtime and routes external traffic to it; Program.cs
# reads $PORT itself and binds Kestrel to http://0.0.0.0:$PORT accordingly.

ENTRYPOINT ["dotnet", "DITEC.Attendance.dll"]
