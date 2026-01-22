#!/usr/bin/env bash
# Wrapper script for git-vcommit that sets up required library paths

# Find OpenSSL library
OPENSSL_PATH=$(nix eval --raw nixpkgs#openssl.out 2>/dev/null || echo "/usr/lib")

# Set LD_LIBRARY_PATH to include OpenSSL
export LD_LIBRARY_PATH="${OPENSSL_PATH}/lib:${LD_LIBRARY_PATH}"

# Get the directory where this script is located
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Execute the actual binary
exec "${SCRIPT_DIR}/git-vcommit-bin" "$@"
