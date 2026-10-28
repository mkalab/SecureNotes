#!/bin/sh
set -eu

if ! command -v gitleaks >/dev/null 2>&1; then
    printf '%s\n' 'Install gitleaks first: https://github.com/gitleaks/gitleaks#installing' >&2
    exit 1
fi

git config core.hooksPath .githooks
printf '%s\n' 'Installed repository hooks from .githooks.'
