#!/bin/bash
# Build and run script for Aspire application

echo -e "\033[0;36mBuilding solution...\033[0m"
dotnet build
if [ $? -ne 0 ]; then
    echo -e "\033[0;31mBuild failed!\033[0m"
    exit 1
fi

echo -e "\033[0;32mStarting Aspire...\033[0m"
aspire run
