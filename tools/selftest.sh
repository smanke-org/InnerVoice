#!/bin/zsh
# Runs the in-app self-test on the sample book. Exit code 0 when every check passed.
# APP_SELFTEST_MODEL=<model.gguf> tools/selftest.sh also tags the sample with a real model and scores it.
set -u
"${0:A:h:h}/.build/app/InnerVoice.app/Contents/MacOS/InnerVoice" --selftest
