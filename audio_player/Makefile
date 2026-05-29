NDK_TOOLCHAIN := $(NDK_PATH)/toolchains/llvm/prebuilt/windows-x86_64
CC := $(NDK_TOOLCHAIN)/bin/aarch64-linux-android26-clang
CFLAGS := -Oz -Wno-unused-parameter

audio_player: audio_player.c
	$(CC) $(CFLAGS) -o $@ $^ -laaudio

clean:
	rm -f audio_player

.PHONY: clean
