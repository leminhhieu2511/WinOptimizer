cmake_minimum_required(VERSION 3.20)
project(DiskMove C)

set(CMAKE_C_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

if(MSVC)
  add_compile_options(/W4 /WX /std:c17)
endif()

enable_testing()
add_subdirectory(DiskMoveCore)
add_subdirectory(tests)