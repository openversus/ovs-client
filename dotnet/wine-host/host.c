/* Loads the built harness plugin the way Ultimate ASI Loader would and shows whether its hooks
   landed. Built and run by run.sh under Wine; see dotnet/README.md.

   The hook sites are written in assembly so their bytes are known: a ten-byte marker
   (mov r11, imm64) followed by the five-byte call or jmp the plugin redirects. */
#include <stdio.h>
#include <windows.h>

__asm__(
	".text\n"
	".intel_syntax noprefix\n"
	".globl twice\n"
	"twice:\n"
	"    lea rax, [rcx+rcx]\n"
	"    ret\n"
	".globl call_site\n"
	"call_site:\n"
	"    sub rsp, 40\n"
	"    mov r11, 0x1122334455667788\n"
	"    call twice\n"
	"    add rsp, 40\n"
	"    ret\n"
	".globl jmp_site\n"
	"jmp_site:\n"
	"    mov r11, 0x8877665544332211\n"
	"    .byte 0xE9\n"
	"    .long twice - . - 4\n"
	".globl guard_site\n"
	"guard_site:\n"
	"    sub rsp, 40\n"
	"    mov r11, 0x1100FFEEDDCCBBAA\n"
	"    call twice\n"
	"    add rsp, 40\n"
	"    ret\n"
	".globl answer\n"
	"answer:\n"
	"    mov eax, 1234\n"
	"    ret\n"
	".att_syntax prefix\n"
);

extern long long twice(long long);
extern long long call_site(long long);
extern long long jmp_site(long long);
extern long long guard_site(long long);
extern int answer(void);

int main(void)
{
	printf("before: call=%lld jmp=%lld guard=%lld answer=%d\n", call_site(21), jmp_site(5), guard_site(3), answer());

	HMODULE asi = LoadLibraryA("OpenVersus.HookTest.asi");
	if (!asi) {
		printf("FAIL: LoadLibraryA(OpenVersus.HookTest.asi) error %lu\n", GetLastError());
		return 2;
	}
	FARPROC init = GetProcAddress(asi, "InitializeASI");
	if (!init) {
		printf("FAIL: no InitializeASI export, error %lu\n", GetLastError());
		return 2;
	}
	init();

	long long c = call_site(21), j = jmp_site(5), g = guard_site(3);
	int a = answer();
	printf("after:  call=%lld jmp=%lld guard=%lld answer=%d\n", c, j, g, a);

	int ok = 1;
	if (c != 43)   { printf("FAIL: call redirect (want 43)\n"); ok = 0; }
	if (j != 1010) { printf("FAIL: jmp redirect (want 1010)\n"); ok = 0; }
	if (g != 77)   { printf("FAIL: guard fallback (want 77)\n"); ok = 0; }
	if (a != 4321) { printf("FAIL: byte patch (want 4321)\n"); ok = 0; }
	printf(ok ? "PASS\n" : "FAILED\n");
	return ok ? 0 : 1;
}
