#!/bin/sh
# Activates the versioned Cyber-Guard hooks in .githooks/
set -e
git config core.hooksPath .githooks
chmod +x .githooks/pre-commit
echo "Cyber-Guard hooks activated (core.hooksPath=.githooks)."
