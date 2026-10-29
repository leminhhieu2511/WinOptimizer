# Win32 wrapper. The sources build once as an object library: DiskMove.dll exports them, and the
# win32_io unit test links the same objects directly (with DMW_STATIC) so it can reach internals.
file(GLOB DMW_SOURCES CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/src/*.c")
add_library(DiskMoveWin32 OBJECT ${DMW_SOURCES})
target_compile_definitions(DiskMoveWin32 PRIVATE DMW_BUILDING UNICODE _UNICODE)
target_include_directories(DiskMoveWin32
  PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
  PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")
target_link_libraries(DiskMoveWin32 PUBLIC DiskMoveCore advapi32)

add_library(DiskMove SHARED $<TARGET_OBJECTS:DiskMoveWin32>)
# Consumers (the CLI) need dm_core.h for the types but must not link the core a second time.
target_include_directories(DiskMove PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
  "$<TARGET_PROPERTY:DiskMoveCore,INTERFACE_INCLUDE_DIRECTORIES>")
target_link_libraries(DiskMove PRIVATE DiskMoveCore advapi32)
