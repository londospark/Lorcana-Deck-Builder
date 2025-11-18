#!/usr/bin/env pwsh
# Run script for Aspire application with automatic rebuild

Write-Host "Configuring Aspire MCP for HTTP (unsecured)" -ForegroundColor Yellow
$env:ASPIRE_DASHBOARD_MCP_ENDPOINT_URL = "http://localhost:16036"
$env:ASPIRE_ALLOW_UNSECURED_TRANSPORT = "true"

Write-Host "Starting Aspire from solution root..." -ForegroundColor Green
aspire run
