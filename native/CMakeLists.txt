cmake_minimum_required(VERSION 3.20)
project(DiskMove C)

set(CMAKE_C_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)
# Static CRT: DiskMove.dll ships next to the app and must not need the VC++ redistributable.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

if(MSVC)
  add_compile_options(/W4 /WX /std:c17)
endif()

enable_testing()
add_subdirectory(DiskMoveCore)
add_subdirectory(DiskMove)
add_subdirectory(DiskMoveCli)
add_subdirectory(tests)
