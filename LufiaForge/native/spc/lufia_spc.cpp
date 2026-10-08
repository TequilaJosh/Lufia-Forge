// C interface to blargg's SNES SPC-700 emulator (from Game_Music_Emu, LGPL 2.1) for Lufia Forge.
// Built as lufia_spc.dll by build.cmd; the emulator sources next to this file are unmodified.
//
// The wrapper keeps its own clock (SPC clocks since the start of the current emulator frame) so the host can
// talk to the sound driver through the ports with time passing between accesses, like the SNES CPU does.
// Sound produced while talking to the driver goes to a scratch buffer; lfspc_render produces audible output.

#include "Snes_Spc.h"
#include "Spc_Filter.h"
#include <string.h>

#define API extern "C" __declspec(dllexport)

enum { scratch_size = 0x4000 };

struct LfSpc
{
	Snes_Spc spc;
	SPC_Filter filter;
	int time;
	short scratch [scratch_size];
};

static void flush( LfSpc* s )
{
	s->spc.end_frame( s->time );
	s->time = 0;
	s->spc.set_output( s->scratch, scratch_size );
}

API LfSpc* lfspc_new()
{
	LfSpc* s = new LfSpc;
	if ( s->spc.init() ) { delete s; return 0; }
	s->filter.clear();
	s->time = 0;
	s->spc.set_output( s->scratch, scratch_size );
	return s;
}

API void lfspc_delete( LfSpc* s ) { delete s; }

// Loads a snapshot in SPC file layout (RAM, DSP registers, CPU registers). Returns 0 or an error message.
API const char* lfspc_load( LfSpc* s, const void* data, long size )
{
	const char* err = s->spc.load_spc( data, size );
	if ( err ) return err;
	s->spc.clear_echo();
	s->filter.clear();
	s->time = 0;
	s->spc.set_output( s->scratch, scratch_size );
	return 0;
}

// Lets the SPC run for the given number of clocks (1.024 MHz) before the next port access.
API void lfspc_wait( LfSpc* s, int clocks )
{
	s->time += clocks;
	if ( s->time >= 0x2000 ) // keep frames short so the scratch buffer never fills
		flush( s );
}

API int  lfspc_read ( LfSpc* s, int port )           { return s->spc.read_port( s->time, port ); }
API void lfspc_write( LfSpc* s, int port, int data ) { s->spc.write_port( s->time, port, data ); }

// Produces count samples (count/2 stereo pairs at 32 kHz) of audible output, through the console's filter.
API void lfspc_render( LfSpc* s, short* out, int count )
{
	flush( s );
	s->spc.set_output( out, count );
	s->spc.end_frame( count / 2 * Snes_Spc::clocks_per_sample );
	s->filter.run( out, count );
	s->spc.set_output( s->scratch, scratch_size );
}

// Copies the 64 KB of sound RAM (for tests).
API void lfspc_ram( LfSpc* s, unsigned char* out )
{
	flush( s );
	memcpy( out, s->spc.smp_ram(), 0x10000 );
}
