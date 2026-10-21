import oracledb
import pyodbc
import os
import winreg
import subprocess
import time
import random
from collections import defaultdict
import sys


ARRAYSIZE = 7  # Array size is 8 (0 to 7)
MAXCOOKIELENGTH = 59
MAXENCRYPTLENGTH = 64
ConstLetter = 65  # for letter "A"
AddTo = 3  # add to encryption string

# Encrypt function
def encrypt(in_string):
    if len(in_string) == 0:
        return ""  # Return OK and empty string if input is empty

    if len(in_string) > MAXCOOKIELENGTH:
        return ""  # Invalid cookie length error

    # Generate the encryption key
    if len(in_string) <= ARRAYSIZE:
        if len(in_string) % 2 == 0:
            key = len(in_string) // 2
        else:
            key = (1 + len(in_string)) // 2
    else:
        random.seed()  # Seed the random number generator
        key = int(random.randint(1, ARRAYSIZE))  # Random value between 1 and ARRAYSIZE

    # Initialize arrays
    code = [[0] * (ARRAYSIZE + 1) for _ in range(ARRAYSIZE + 1)]
    ecode = [0] * ((ARRAYSIZE + 1) * (ARRAYSIZE + 1))

    # Store the key
    code[0][0] = key + ConstLetter

    # Store the string into a NXN array
    i = -1
    m = 0
    while m < len(in_string):
        if i == -1:
            j = 1
        else:
            j = 0
        i += 1
        while j <= ARRAYSIZE and m < len(in_string):
            code[i][j] = ord(in_string[m])
            j += 1
            m += 1

    # Padding with blanks if the message is less than 64 characters
    for n in range(j, ARRAYSIZE + 1):
        code[i][n] = ord(" ")

    for n in range(i + 1, ARRAYSIZE + 1):
        for l in range(ARRAYSIZE + 1):
            code[n][l] = ord(" ")

    # Store the key
    ecode[0] = key + ConstLetter

    # Copy from 2D array to 1D array encrypted
    index = 1
    for j in range(key, ARRAYSIZE + 1):
        for i in range(ARRAYSIZE + 1):
            ecode[index] = code[i][j]
            index += 1

    for j in range(key):
        for i in range(ARRAYSIZE + 1):
            if not (i == 0 and j == 0):
                ecode[index] = code[i][j]
                index += 1

    # Convert to fixed-length string
    e_string = chr(int(ecode[0]))
    for j in range(1, 64):
        e_string += chr(int(ecode[j] + AddTo))

    return e_string  # Return OK and encrypted string

# Decrypt function
def decrypt(encrypted_string):
    if len(encrypted_string) == 0:
        return ""  # Return OK and empty string if input is empty

    if len(encrypted_string) != MAXENCRYPTLENGTH:
        return ""  # Decrypt fail error

    # Get the key
    key = ord(encrypted_string[0]) - ConstLetter

    # Initialize arrays
    ecode = [[0] * (ARRAYSIZE + 1) for _ in range(ARRAYSIZE + 1)]
    dcode = [0] * ((ARRAYSIZE + 1) * (ARRAYSIZE + 1))

    # Decrypt as we go
    index = 1
    for j in range(key, ARRAYSIZE + 1):
        for i in range(ARRAYSIZE + 1):
            dcode[i * (ARRAYSIZE + 1) + j] = ord(encrypted_string[index]) - AddTo
            index += 1

    for j in range(key):
        for i in range(ARRAYSIZE + 1):
            if not (i == 0 and j == 0):
                dcode[i * (ARRAYSIZE + 1) + j] = ord(encrypted_string[index]) - AddTo
                index += 1

    # Convert to output string
    out_string = ""
    for i in range(1, (ARRAYSIZE + 1) * ARRAYSIZE):
        out_string += chr(int(dcode[i]))

    return out_string.strip()  # Return OK and trimmed output string

def delfolder(path, clean_msg=''):
    if not os.path.exists(path):
        return
    msg = clean_msg if clean_msg!='' else path
    print(f"\rDeleting: {msg}", end='', flush=True)
    subprocess.run([
        "powershell", "-Command",
        f"Remove-Item -Path '{path}' -Recurse -Force -ErrorAction SilentlyContinue"
    ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    print('\r', end='', flush=True)
    parent = os.path.dirname(path)
    while True:
        if not os.path.exists(parent):
            break
        try:
            if len(os.listdir(parent)) == 0:
                subprocess.run([
                    "powershell", "-Command",
                    f"Remove-Item -Path '{parent}' -Recurse -Force -ErrorAction SilentlyContinue"
                ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                parent = os.path.dirname(parent)
            else:
                break
        except Exception as e:
            print('\nException occured:',e)
            break
def checkandkillsession(schema):
    cursor.execute("""
        SELECT sid, serial#
        FROM v$session
        WHERE username = :1
    """, [schema.upper()])
    sessions = cursor.fetchall()
    for sid, serial in sessions:
        try:
            cursor.execute(f"ALTER SYSTEM KILL SESSION '{sid},{serial}' IMMEDIATE")
        except Exception as e:
            print(f"Failed to kill session {sid},{serial}: {e}")
def countofitems(path):
    count = 0
    if os.path.exists(path):
        for _, dirs, files in os.walk(path):
            count += len(dirs) + len(files)
    return count
def get_drive_commonpaths(paths):
    drive_map = defaultdict(list)
    for path in paths:
        if os.path.exists(path):
            drive, _ = os.path.splitdrive(path)
            drive_map[drive].append(path)
    commonpaths = []
    for path_list in drive_map.values():
        try:
            commonpaths.append(os.path.commonpath(path_list))
        except ValueError:
            continue
    count = 0
    for i in paths:
        if os.path.isdir(i):
            count += countofitems(i)
    for i in commonpaths:
        if count >= countofitems(i):
            continue
        else:
            commonpaths.remove(i)
    return commonpaths
def askuserplants():
    while True:
        inp = input(
            "Select Plant (1 for first plant, 2 for second plant..) or enter multiple plants separated by comma (Ex: 1,2,3)\n"
            "You can also type 'all' to delete all plants in the site\n"
            "(Warning: Selecting 'all' or multiple plants option will delete all the selected plants, projects, and reference data folders automatically): "
        ).strip().lower().split(",")
        if inp == ['exit']:
            exitapp()
        if inp == ['all']:
            return inp
        if all(i.strip().isnumeric() for i in inp):
            return [i.strip() for i in inp]
        print("Invalid input. Please enter valid numbers (1,2,3) or 'all'.\n")
def userconfirm():
    while True:
        inp = input("Select 1 for Yes and 0 for No: ").strip().lower()
        if inp == 'exit':
            exitapp()
        if inp in ('1', '0'):
            return int(inp)
        else:
            print("Invalid input. Please enter either 1 or 0.")
def aestheticdel(paths):
    max_len = 0
    for i in paths:
        clean_msg = i.ljust(max_len)
        delfolder(i,clean_msg)
        max_len = max(max_len, len(i))
def getinipath():
    while True:
        raw_input_path = input("Give the smartplantv4.ini Path: ").strip('"')
        if raw_input_path == 'exit':
            exitapp()
        input_path = os.path.abspath(raw_input_path)
        if os.path.isdir(input_path):
            inipath = os.path.join(input_path, 'smartplantv4.ini')
        else:
            inipath = input_path
        if os.path.isfile(inipath) and inipath.endswith('smartplantv4.ini'):
            return inipath
        else:
            print("Invalid smartplantv4.ini path. Please type the correct path again.")
def get_affiliated_tablespaces(schema_list):
    """
    Gets tablespaces used by the given schemas.
    Returns a set of tablespaces and a mapping of tablespace → owners.
    """
    placeholder = ','.join(f"'{s.upper()}'" for s in schema_list)
    query = f"SELECT tablespace_name, owner FROM dba_segments WHERE owner IN ({placeholder})"
    cursor.execute(query)
    ts_usage = {}
    for tablespace, owner in cursor.fetchall():
        ts_usage.setdefault(tablespace, set()).add(owner)
    return set(ts_usage.keys()), ts_usage
def get_all_users_for_tablespaces(tablespaces):
    """
    Returns a mapping of tablespace → all owners currently using it (post-drop check).
    """
    if not tablespaces:
        return {}
    if len(tablespaces)>1:
        query = f"SELECT distinct tablespace_name, owner FROM dba_segments WHERE tablespace_name IN {tuple(tablespaces)}"
    else:
        query = f"SELECT distinct tablespace_name, owner FROM dba_segments WHERE tablespace_name = ('{tuple(tablespaces)[0]}')"
    cursor.execute(query)
    ts_users = {}
    for tablespace, owner in cursor.fetchall():
        ts_users.setdefault(tablespace, set()).add(owner)
    return ts_users
def purge_recyclebin():
    cursor.execute("PURGE RECYCLEBIN")
def drop_unused_tablespaces(original_schema_list, ts_list_to_check):
    """
    After schemas are dropped, re-check if any of the original tablespaces are now unused.
    If unused (no other owner besides dropped schemas), drop them safely.
    """
    purge_recyclebin()
    ts_users = get_all_users_for_tablespaces(ts_list_to_check)
    if not ts_users:
        for i in ts_list_to_check:
            try:
                cursor.execute(f"DROP TABLESPACE {i} INCLUDING CONTENTS AND DATAFILES CASCADE CONSTRAINTS")
                print(f"Tablespace {i} dropped.")
            except oracledb.DatabaseError as e:
                print(f"Failed to drop tablespace {i}: {e}")
    for ts, owners in ts_users.items():
        active_owners = [u for u in owners if u.upper() not in map(str.upper, original_schema_list)]
        if not active_owners:
            try:
                cursor.execute(f"DROP TABLESPACE {ts} INCLUDING CONTENTS AND DATAFILES CASCADE CONSTRAINTS")
                print(f"Tablespace {ts} dropped.")
            except oracledb.DatabaseError as e:
                print(f"Failed to drop tablespace {ts}: {e}")
        else:
            print(f"Skipped dropping {ts} — still used by: {', '.join(active_owners)}")
def get_temp_tablespaces_for_schemas(schema_list):
    """
    Get temp tablespaces used by the given schemas from DBA_USERS.
    """
    placeholder = ','.join(f"'{s.upper()}'" for s in schema_list)
    query = f"SELECT DISTINCT temporary_tablespace FROM dba_users WHERE username IN ({placeholder})"
    cursor.execute(query)
    return {row[0] for row in cursor.fetchall() if row[0]}
def is_temp_tablespace_in_use(temp_ts_name):
    """
    Check if a temporary tablespace is currently in use.
    """
    query = f"SELECT COUNT(*) FROM v$sort_usage WHERE tablespace = :tsname"
    cursor.execute(query, {'tsname': temp_ts_name})
    count = cursor.fetchone()[0]
    return count > 0
def drop_unused_temp_tablespaces(temp_ts_set):
    """
    Drop temporary tablespaces if they are not in active use.
    """
    for temp_ts in temp_ts_set:
        if is_temp_tablespace_in_use(temp_ts):
            print(f"Skipped dropping Temporary tablespace {temp_ts} — currently in use.")
            continue
        try:
            cursor.execute(f"DROP TABLESPACE {temp_ts} INCLUDING CONTENTS AND DATAFILES CASCADE CONSTRAINTS")
            print(f"Temporary Tablespace {temp_ts} dropped.")
        except oracledb.DatabaseError as e:
            print(f"Failed to drop temporary tablespace {temp_ts}: {e}")
def exitapp():
    try:
        conn.close()
    except Exception:
        pass
    sys.exit(0)
while True: 
    print('Delete Plant Utility v4')
    time.sleep(1)
    print("Press 'exit' anytime to quit the application")
    time.sleep(1)
    responce = input('Press anything to Continue ').strip().lower() 
    if responce == 'exit':
        exitapp()
    else: 
        inipath = getinipath()
        time.sleep(0.5)
        with open(inipath, 'r', encoding='utf-16') as file:
            lines = file.readlines()
        SiteSchema = lines[5].split('=')[1].strip()
        dbtype = lines[2].split('=')[1].strip()
        if dbtype=='ORACLE':            
            Oracle_Instance = lines[4].split('=')[1].strip()
            username = lines[7].split('=')[1].strip()
            encpassword = lines[8].split('=')[1].strip()
            password = decrypt(encpassword)
            def find_oracle_clients():
                paths = []
                registry_path = r'SOFTWARE\WOW6432NODE\ORACLE'
                try:
                    with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, registry_path) as key:
                        i = 0
                        while True:
                            try:
                                subkey_name = winreg.EnumKey(key, i)
                                if 'Home' in subkey_name:
                                    subkey_path = f'{registry_path}\\{subkey_name}'
                                    subkey = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, subkey_path)
                                    try:
                                        install_path = winreg.QueryValueEx(subkey, 'ORACLE_HOME')[0]
                                        paths.append(install_path)
                                    except FileNotFoundError:
                                        pass
                                i += 1
                            except OSError:
                                break
                except FileNotFoundError:
                    pass
                return paths
            oracle_paths = find_oracle_clients()
            nooraclepaths = 0
            if not oracle_paths:
                nooraclepaths = 1
            if oracle_paths:
                oracle_home = oracle_paths[0]
                print(f'Oracle 32-bit client home found at {oracle_home}')
            if oracle_home:
                def get_tnsnames_path(oracle_home):
                    tnsnames_path = os.path.join(oracle_home, 'network', 'admin', 'tnsnames.ora')
                    return tnsnames_path
                tnsnames_path = get_tnsnames_path(oracle_home)
                tnsnames_dir = os.path.dirname(tnsnames_path)
                if not os.path.exists(tnsnames_path):
                    raise RuntimeError("tnsnames.ora file not found.")
            time.sleep(0.5)
            try:
                if nooraclepaths:
                    print(f'No Oracle 32-bit clients found')
                    hostname = input('Enter the host name: ')
                    port=input('Enter the port of the instance: ')
                    service_name = input('Enter service name: ')
                    dsn = f"(DESCRIPTION=(ADDRESS_LIST=(ADDRESS=(PROTOCOL=TCP)(HOST={hostname})(PORT={port})))(CONNECT_DATA=(SERVICE_NAME={service_name})))"
                    print(f'Connecting to {service_name} service as {username}')
                    conn = oracledb.connect(user=username, password=password, dsn=dsn)
                else:
                    print(f"Connecting to Oracle instance {Oracle_Instance} as {username}")
                    oracledb.init_oracle_client(config_dir=tnsnames_dir)
                    conn = oracledb.connect(
                        user=username,
                        password=password,
                        dsn=Oracle_Instance,
                        config_dir=tnsnames_dir
                    )
                cursor = conn.cursor()
                #Changing the default schema to the site schema (Not required)
                query = f"alter session set current_schema = {SiteSchema}"
                cursor.execute(query)
                # Getting the list of plant names in the site
                query1 = f"select sp_id, Name, sp_parentrootitemid from T_RootItem where SP_ParentRootItemID ='0' or sp_parentrootitemid is null"
                cursor.execute(query1)
                plantnamecursor = cursor.fetchall()
                print('List of plants available in the site: ')
                time.sleep(0.5)
                plantnumber=0
                for sp_id, Name, parentrootitemid in plantnamecursor:
                    plantnumber+=1
                    print(f'{plantnumber}. {Name}')
                #select the plant name
                time.sleep(0.5)
                plantselectionscheck = askuserplants()
                # Getting the list of plant and project sp_ids for the selected plant.
                if 'all' in plantselectionscheck:
                    gtbslist=set()
                    gttbslist= []
                    guserlist = []
                    plantselections = [i for i in range(1,plantnumber+1)]
                elif len(plantselectionscheck)>1:
                    gtbslist=set()
                    gttbslist = []
                    guserlist = []
                    plantselections = [int(inte) for inte in plantselectionscheck]
                else:
                    plantselections = [int(inte) for inte in plantselectionscheck]
                for plantselection in plantselections:
                    plantnamelist = list(plantnamecursor)
                    plantsp_id = plantnamelist[plantselection-1][0]
                    cursor.execute(f"select Name from t_rootitem where sp_id = '{plantsp_id}'")
                    print(f"Selected plant is {cursor.fetchall()[0][0]}")
                    time.sleep(0.5)
                    projectschemaquery = f"select sp_id from t_rootitem where sp_parentrootitemid = '{plantsp_id}'"
                    cursor.execute(projectschemaquery)
                    projectsp_idlist = [i[0] for i in list(cursor.fetchall())]
                    justprojectsp_ids = tuple(projectsp_idlist)
                    projectsp_idlist.append(plantsp_id)
                    projectsp_idlist = tuple(projectsp_idlist)
                    #getting the list of plant schemas to drop
                    if len(projectsp_idlist)==1:
                        plantschemaquery = f"select username from t_db_data where sp_rootitemid = '{projectsp_idlist[0]}'"
                    else:                        
                        plantschemaquery = f"select username from t_db_data where sp_rootitemid in {projectsp_idlist}"
                    cursor.execute(plantschemaquery)
                    plantschemacursor = cursor.fetchall()
                    plantschemalist = [i[0] for i in list(plantschemacursor)]
                    dropschemaslist = []
                    for i in plantschemalist:
                        checkschemaquery = f"select username from all_users where username = '{i.upper()}'"
                        cursor.execute(checkschemaquery)
                        if cursor.fetchone() != None:
                            dropschemaslist.append(i)
                        else:
                            None
                    tbspacelist, _ = get_affiliated_tablespaces(dropschemaslist) if len(dropschemaslist)==len(plantschemalist) else get_affiliated_tablespaces(plantschemalist)
                    temptbspacelist = get_temp_tablespaces_for_schemas(dropschemaslist) if len(dropschemaslist)==len(plantschemalist) else get_temp_tablespaces_for_schemas(plantschemalist)
                    print(f'List of schemas to drop: {dropschemaslist}')
                    time.sleep(0.5)
                    # getting the list of schemas from which we can get the reference data path for each plant
                    if len(projectsp_idlist)==1:
                        appusernamequery = f"select username from t_db_data where sp_rootitemid = '{projectsp_idlist[0]}' and sp_schema_type in ('SPEL','SPPID')"
                    else:
                        appusernamequery = f"select username from t_db_data where sp_rootitemid in {projectsp_idlist} and sp_schema_type in ('SPEL','SPPID')"
                    cursor.execute(appusernamequery)
                    appusernamelist = [i[0] for i in list(cursor.fetchall())]
                    # getting the ref data paths
                    pidrefvaluepathlist = []
                    for i in appusernamelist:
                        if i in dropschemaslist:
                            pidvaluepathquery = f"select value from {i}.t_optionsetting where sp_id in ('680','681','682','683','684','685','703','709','804','809','824')"
                            cursor.execute(pidvaluepathquery)
                            pidrefvaluepathlist.extend([j[0] for j in list(cursor.fetchall())])
                    elrefvaluepathlist = []
                    for i in appusernamelist:
                        if i in dropschemaslist:
                            elvaluepathquery = f"select value from {i}.t_optionsetting where sp_id in ('1001','1002','1004','1005','1008','1010','1011','1018','1022','1023','1024','1034','1035','1040','1047','1048','1049','1050','1051','1052','1054','1055','1061','1063','1074','1075','1076')"
                            cursor.execute(elvaluepathquery)
                            elrefvaluepathlist.extend([j[0] for j in list(cursor.fetchall())])
                    # creating a common path for all the ref data paths and deleting them.
                    if pidrefvaluepathlist:
                        pidcommon_refvaluepaths = get_drive_commonpaths(pidrefvaluepathlist)
                        for i in pidcommon_refvaluepaths:
                            print(f"P&ID Reference Data folder to get deleted: {i}")
                        time.sleep(0.5)
                    if elrefvaluepathlist: 
                        elcommon_refvaluepaths = get_drive_commonpaths(elrefvaluepathlist)
                        for i in elcommon_refvaluepaths:
                            print(f"SEL Reference Data folder to get deleted: {i}")
                        time.sleep(0.5)
                    if len(justprojectsp_ids) == 1:
                        projectfolderquery = f"select path from t_rootitem where sp_id = '{justprojectsp_ids[0]}'"
                    elif len(justprojectsp_ids)==0:
                        None
                    else:
                        projectfolderquery = f"select path from t_rootitem where sp_id in {justprojectsp_ids}"
                    # deleting the project folders
                    try:
                        cursor.execute(projectfolderquery)
                        oraprojfolderlist = [i[0] for i in list(cursor.fetchall())]                        
                        print(f"List of Project folders to get deleted: ")
                        time.sleep(0.5)
                        for i in oraprojfolderlist:
                            print(i)
                        time.sleep(0.5)
                    except NameError:
                        None
                    # getting the schemas of only the plants.
                    plantfolderquery = f"select path from t_rootitem where sp_id = '{plantsp_id}'"
                    cursor.execute(plantfolderquery)
                    plantfolderpath = cursor.fetchall()[0][0]
                    # deleting the plant folder
                    print(f'Plant folder to get deleted: {plantfolderpath}')
                    time.sleep(0.5)
                    if 'all' in plantselectionscheck or len(plantselectionscheck)>1:
                        userconfirmation = 1
                    else:
                        print('Are you sure you want to delete the plant and the projects?\nThis will delete the plant and project schemas and removes the physical files.\nThis step is irreversible.')
                        userconfirmation = userconfirm()
                    if userconfirmation:
                        time.sleep(0.5)
                        if pidrefvaluepathlist:
                            print('\nDeleting P&ID Reference Data folder........')
                            aestheticdel(pidcommon_refvaluepaths)
                            time.sleep(0.5)
                        if elrefvaluepathlist:
                            print('\nDeleting SEL Reference Data folder......')
                            aestheticdel(elcommon_refvaluepaths)
                            time.sleep(0.5)
                        try:
                            print('\nDeleting Project Folders....')
                            aestheticdel(oraprojfolderlist)
                            print('\nProject folders deleted')
                            time.sleep(0.5)
                        except NameError:
                            None
                        print('\nDeleting Plant folder.....')
                        delfolder(plantfolderpath)
                        time.sleep(0.5)
                        print('\nDropping the schemas....')
                        time.sleep(0.5)
                        # dropping all the schemas
                        for i in dropschemaslist:
                            try:
                                dropschemaquery = f"drop user {i} cascade"
                                cursor.execute(dropschemaquery)
                            except oracledb.DatabaseError as e:
                                eobj, = e.args
                                if eobj.code == 1940:
                                    checkandkillsession(i)
                                    dropschemaquery = f"drop user {i} cascade"
                                    cursor.execute(dropschemaquery)
                        print('Database schemas and physical files are deleted successfully!')
                        time.sleep(0.5)
                        if 'all' in plantselectionscheck or len(plantselectionscheck)>1:
                            gtbslist.update(tbspacelist)
                            gttbslist.extend(temptbspacelist)
                            guserlist.extend(plantschemalist)
                        else:
                            if len(tbspacelist)>0 or len(temptbspacelist)>0:
                                print('Do you want to drop the schema-associated tablespaces?')
                                tbsdropconfirm = userconfirm()
                                if tbsdropconfirm:
                                    drop_unused_tablespaces(plantschemalist,tbspacelist)
                                    drop_unused_temp_tablespaces(temptbspacelist)
                                    print('Tablespaces removed successfully')
                        print("Deleting removed plant's information from the Site tables......")
                        time.sleep(0.5)
                        if len(projectsp_idlist)==1:
                            rootitemdelquery = f"delete from t_rootitem where sp_id = '{projectsp_idlist[0]}'"
                            db_datadelquery = f"delete from t_db_data where sp_rootitemid = '{projectsp_idlist[0]}'"
                            rolessp_idquery = f"select sp_id from t_roles where proj_id = '{projectsp_idlist[0]}'"
                        else:                            
                            rootitemdelquery = f"delete from t_rootitem where sp_id in {projectsp_idlist}"
                            db_datadelquery = f"delete from t_db_data where sp_rootitemid in {projectsp_idlist}"
                            rolessp_idquery = f"select sp_id from t_roles where proj_id in {projectsp_idlist}"
                        cursor.execute(rolessp_idquery)
                        rolessp_idlist = []
                        rolessp_idlist.extend([i[0] for i in list(cursor.fetchall())])
                        rolessp_idlist = tuple(rolessp_idlist)
                        if len(projectsp_idlist)==1:
                            rolesdelquery = f"delete from t_roles where proj_id = '{projectsp_idlist[0]}'"
                            roleusersdelquery = f"delete from t_roleusers where role_id = '{rolessp_idlist[0]}'"
                            rolerightsdelquery = f"delete from t_rolerights where role_id = '{rolessp_idlist[0]}'"
                        else:
                            rolesdelquery = f"delete from t_roles where proj_id in {projectsp_idlist}"
                            roleusersdelquery = f"delete from t_roleusers where role_id in {rolessp_idlist}"
                            rolerightsdelquery = f"delete from t_rolerights where role_id in {rolessp_idlist}"
                        try:
                            cursor.execute(rootitemdelquery)
                            cursor.execute(db_datadelquery)
                            cursor.execute(rolesdelquery)
                            cursor.execute(roleusersdelquery)
                            cursor.execute(rolerightsdelquery)
                            conn.commit()
                        except oracledb.DatabaseError as e:
                            print(f'The following error occurred: {e}')
                            conn.rollback()
                        print('Plant has been deleted successfully!')   
                        time.sleep(1)
                if 'all' in plantselectionscheck or len(plantselectionscheck)>1:
                    gttbslist = list(set(gttbslist))
                    if len(gtbslist)>0 or len(gttbslist)>0:
                        print('Do you want to drop all the schema-associated tablespaces?')
                        tbsdropconfirm = userconfirm()
                        if tbsdropconfirm:
                            drop_unused_tablespaces(guserlist, gtbslist)
                            drop_unused_temp_tablespaces(gttbslist)
                            print('Tablespaces removed successfully')
                cursor.close()
                conn.close()

            except oracledb.DatabaseError as e:
                print(f"Database error occurred: {e}")

            except Exception as e:
                print(f"An error occurred: {e}")
        else:
        
            SiteDatabase = lines[4].split('=')[1].strip()
        
            SQLServer = lines[3].split('=')[1].strip()
            username = lines[7].split('=')[1].strip()
            encpassword = lines[8].split('=')[1].strip()
            password = decrypt(encpassword)
            driver = '{ODBC Driver 11 for SQL Server}'
            masterdb = 'master'
            time.sleep(0.5)
            print(f"Connecting to SQL Server at {SQLServer} as {username}")
            try:
                conn_string = f"DRIVER={driver};SERVER={SQLServer};DATABASE={masterdb};UID={username};PWD={password}"
                try:
                    conn = pyodbc.connect(conn_string,autocommit=True)
                except pyodbc.Error as sqlstate:
                    if sqlstate.args[0] == 'IM002':
                        try:
                            driver = '{ODBC Driver 17 for SQL Server}'
                            conn_string = f"DRIVER={driver};SERVER={SQLServer};DATABASE={masterdb};UID={username};PWD={password}"
                            conn = pyodbc.connect(conn_string,autocommit=True)
                        except pyodbc.Error as sqlstate:
                            if sqlstate.args[0] == 'IM002':
                                try:
                                    driver = '{ODBC Driver 18 for SQL Server}'
                                    conn_string = f"DRIVER={driver};SERVER={SQLServer};DATABASE={masterdb};UID={username};PWD={password}"
                                    conn = pyodbc.connect(conn_string,autocommit=True)
                                except pyodbc.Error as sqlstate:
                                    print(f'Failed to connect: {sqlstate.args[0]} - {sqlstate.args[1]}')
                            else:
                                print(f'Failed to connect: {sqlstate.args[0]} - {sqlstate.args[1]}')
                    else:
                        print(f'Failed to connect: {sqlstate.args[0]} - {sqlstate.args[1]}')
                cursor = conn.cursor()
                # Getting the list of plant names in the site
                cursor.execute(f"select sp_id, Name from {SiteDatabase}.{SiteSchema}.T_RootItem where SP_ParentRootItemID ='0' or sp_parentrootitemid is null")
                plantnamecursor = cursor.fetchall()
                print('List of Plants available in the site: ')
                time.sleep(0.5)
                plantnumber=0
                for sp_id, Name in plantnamecursor:
                    plantnumber+=1
                    print(f'{plantnumber}. {Name}')
                #select the plant name
                plantselectionscheck = askuserplants()
                time.sleep(0.5)
                # Getting the list of plant and project sp_ids for the selected plant.
                if 'all' in plantselectionscheck:
                    plantselections = [i for i in range(1,plantnumber+1)]
                else:
                    plantselections = [int(inte) for inte in plantselectionscheck]
                for plantselection in plantselections:
                    plantrootitemid = list(plantnamecursor)[plantselection-1][0]
                    cursor.execute(f"select name from {SiteDatabase}.{SiteSchema}.t_rootitem where sp_id = '{plantrootitemid}'")
                    plantname = cursor.fetchall()[0][0]
                    cursor.execute(f"select db_id('{plantname}')")
                    print(f'Plant selected to delete: {plantname}')
                    time.sleep(0.5)
                    db_id = cursor.fetchone()[0]
                    if db_id != None:
                        projectschemaquery = f"select sp_id from {SiteDatabase}.{SiteSchema}.t_rootitem where sp_parentrootitemid = '{plantrootitemid}'"
                        cursor.execute(projectschemaquery)
                        plprojsp_idlist = [i[0] for i in list(cursor.fetchall())]
                        justprojsp_ids = tuple(plprojsp_idlist)
                        plprojsp_idlist.append(plantrootitemid)
                        if len(plprojsp_idlist)==1:
                            appdbandschemaquery = f"select username, data_base from {SiteDatabase}.{SiteSchema}.T_DB_DATa where sp_rootitemid = '{tuple(plprojsp_idlist)[0]}' and sp_schema_type in ('SPEL','SPPID')"
                        else:
                            appdbandschemaquery = f"select username, data_base from {SiteDatabase}.{SiteSchema}.T_DB_DATa where sp_rootitemid in {tuple(plprojsp_idlist)} and sp_schema_type in ('SPEL','SPPID')"
                        cursor.execute(appdbandschemaquery)
                        plantprojdbschemalist = list(cursor.fetchall())
                        plantprojdblist =[i[1] for i in plantprojdbschemalist]
                        plantprojschemalist = [i[0] for i in plantprojdbschemalist]
                        pidrefdatapathslist = []
                        for i,j in zip(plantprojdblist,plantprojschemalist):
                            pidrefdatapathquery = f"select value from {i}.{j}.t_optionsetting where sp_id in ('680','681','682','683','684','685','703','709','804','809','824')"
                            cursor.execute(pidrefdatapathquery)
                            pidrefdatapathslist.extend([k[0] for k in list(cursor.fetchall())])
                        elrefdatapathslist = []
                        for i,j in zip(plantprojdblist,plantprojschemalist):
                            elrefdatapathquery = f"select value from {i}.{j}.t_optionsetting where sp_id in ('1001','1002','1004','1005','1008','1010','1011','1018','1022','1023','1024','1034','1035','1040','1047','1048','1049','1050','1051','1052','1054','1055','1061','1063','1074','1075','1076')"
                            cursor.execute(elrefdatapathquery)
                            elrefdatapathslist.extend([k[0] for k in list(cursor.fetchall())])
                        if pidrefdatapathslist:
                            pidcommonrefdatapaths = get_drive_commonpaths(pidrefdatapathslist)
                            for i in pidcommonrefdatapaths:
                                print(f"P&ID Reference Data folder to get deleted: {i}")
                            time.sleep(0.5)
                        if elrefdatapathslist:
                            elcommonrefdatapaths = get_drive_commonpaths(elrefdatapathslist)
                            for i in elcommonrefdatapaths:
                                print(f"SEL Reference Data folder to get deleted: {i}")
                            time.sleep(0.5)
                        if len(justprojsp_ids)==1:
                            projfolderquery = f"select path from {SiteDatabase}.{SiteSchema}.t_Rootitem where sp_id = '{justprojsp_ids[0]}'"
                        elif len(justprojsp_ids)==0:
                            None                        
                        else:
                            projfolderquery = f"select path from {SiteDatabase}.{SiteSchema}.t_rootitem where sp_id in {tuple(justprojsp_ids)}"
                        try:
                            projfolderlist = []
                            cursor.execute(projfolderquery)
                            projfolderlist.extend([k[0] for k in list(cursor.fetchall())])
                            print(f"Projects folders to get deleted: ")
                            time.sleep(0.5)
                            for i in projfolderlist:
                                print(i)
                            time.sleep(0.5)
                        except NameError:
                            None
                        plantfolderquery = f"select path from {SiteDatabase}.{SiteSchema}.t_rootitem where sp_id ='{plantrootitemid}'"
                        cursor.execute(plantfolderquery)
                        plantfolderpath = cursor.fetchall()[0][0]
                        print(f"Plant folder to get deleted: {plantfolderpath}")
                        dropdbquery = f"select data_base from {SiteDatabase}.{SiteSchema}.t_db_data where sp_rootitemid = '{plantrootitemid}'"
                        cursor.execute(dropdbquery)
                        dropdblist = cursor.fetchall()[0][0]
                        print(f"Database to drop: {dropdblist}")
                        time.sleep(0.5)
                        if 'all' in plantselectionscheck or len(plantselectionscheck)>1:
                            userconfirmation = 1
                        else:
                            print('Are you sure you want to delete the plant and the projects?\nThis will delete the plant database and removes the physical files.\nThis step is irreversible.')
                            userconfirmation = userconfirm()
                        if userconfirmation:
                            time.sleep(0.5)
                            if pidrefdatapathslist:
                                print('\nDeleting P&ID Reference Data folder........')
                                aestheticdel(pidcommonrefdatapaths)
                                time.sleep(0.5)
                            if elrefdatapathslist:
                                print('\nDeleting SEL Reference Data folder......')
                                aestheticdel(elcommonrefdatapaths)
                                time.sleep(0.5)
                            try:
                                print('\nDeleting Project Folders....')
                                time.sleep(0.5)
                                aestheticdel(projfolderlist)
                                print('\nProject folders deleted')
                            except NameError:
                                None
                            print('Deleting Plant folder.....')
                            time.sleep(0.5)
                            delfolder(plantfolderpath)
                            print('\nDropping the database....')
                            time.sleep(0.5)
                            try:
                                backuphistquery = f"exec msdb.dbo.sp_delete_database_backuphistory @database_name = N'{dropdblist}'"
                                cursor.execute(backuphistquery)                
                                terminate_query = f"ALTER DATABASE {dropdblist} SET SINGLE_USER WITH ROLLBACK IMMEDIATE"
                                cursor.execute(terminate_query)
                                print(f"Terminated active conns to {dropdblist}")    
                                time.sleep(0.5)            
                                cursor.execute('use master')
                                cursor.execute(f"DROP DATABASE {dropdblist}")
                                print(f'Database {dropdblist} dropped successfully!')
                                time.sleep(0.5)
                            except pyodbc.Error as sqlstate:
                                print(f'Failed to execute SQL command. SQLSTATE: {sqlstate.args[0]} - {sqlstate.args[1]}')
                            except Exception as e:
                                print(f'Exception occurred: {e}')    
                            if len(plprojsp_idlist)==1:
                                droploginquery = f"select username from {SiteDatabase}.{SiteSchema}.T_DB_Data where sp_rootitemid = '{tuple(plprojsp_idlist)[0]}'"
                            else:    
                                droploginquery = f"select username from {SiteDatabase}.{SiteSchema}.T_DB_Data where sp_rootitemid in {tuple(plprojsp_idlist)}"
                            cursor.execute(droploginquery)
                            droploginlist = list(cursor.fetchall())
                            print('Dropping logins......')
                            time.sleep(0.5)
                            for i in droploginlist:
                                cursor.execute(f"use master")
                                cursor.execute(f"drop login {i[0]}")
                            print(f'Logins dropped successfully!')
                            time.sleep(0.5)
                            print("Deleting removed plant's information from the Site tables......")
                            time.sleep(0.5)
                            conn.autocommit= False
                            if len(plprojsp_idlist)==1:
                                rootitemdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_rootitem where sp_id = '{tuple(plprojsp_idlist)[0]}'"
                                db_datadelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_db_data where sp_rootitemid = '{tuple(plprojsp_idlist)[0]}'"
                                rolessp_idquery = f"select sp_id from {SiteDatabase}.{SiteSchema}.t_roles where proj_id = '{tuple(plprojsp_idlist)[0]}'"
                            else:
                                rootitemdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_rootitem where sp_id in {tuple(plprojsp_idlist)}"
                                db_datadelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_db_data where sp_rootitemid in {tuple(plprojsp_idlist)}"
                                rolessp_idquery = f"select sp_id from {SiteDatabase}.{SiteSchema}.t_roles where proj_id in {tuple(plprojsp_idlist)}"
                            cursor.execute(rolessp_idquery)
                            rolessp_idlist = []
                            rolessp_idlist.extend([i[0] for i in list(cursor.fetchall())])
                            rolessp_idlist = tuple(rolessp_idlist)
                            if len(rolessp_idlist)==1:
                                rolesdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_roles where proj_id = '{tuple(plprojsp_idlist)[0]}'"
                                roleusersdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_roleusers where role_id = '{rolessp_idlist[0]}'"
                                rolerightsdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_rolerights where role_id = '{rolessp_idlist[0]}'"
                            else: 
                                rolesdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_roles where proj_id in {tuple(plprojsp_idlist)}"
                                roleusersdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_roleusers where role_id in {rolessp_idlist}"
                                rolerightsdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_rolerights where role_id in {rolessp_idlist}"
                            try:
                                cursor.execute(rootitemdelquery)
                                cursor.execute(db_datadelquery)
                                cursor.execute(rolesdelquery)
                                cursor.execute(roleusersdelquery)
                                cursor.execute(rolerightsdelquery)
                                conn.commit()
                            except pyodbc.DatabaseError as e:
                                print(f'The following error occurred: {e}')
                                conn.rollback()
                            conn.autocommit= True
                            print('\nPlant has been deleted successfully!\n')
                            time.sleep(1)
                    else:
                        print('Database not available. Deleting Plant and Project folders and dropping information from site.')
                        time.sleep(0.5)
                        projectschemaquery = f"select sp_id from {SiteDatabase}.{SiteSchema}.t_rootitem where sp_parentrootitemid = '{plantrootitemid}'"
                        cursor.execute(projectschemaquery)
                        plprojsp_idlist = [i[0] for i in list(cursor.fetchall())]
                        justprojsp_ids = tuple(plprojsp_idlist)
                        plprojsp_idlist.append(plantrootitemid)
                        if len(plprojsp_idlist)==1:
                            cursor.execute(f"select path from {SiteDatabase}.{SiteSchema}.T_RootItem where sp_id = '{tuple(plprojsp_idlist)[0]}'")
                        else:
                            cursor.execute(f"select path from {SiteDatabase}.{SiteSchema}.T_RootItem where sp_id in {tuple(plprojsp_idlist)}")
                        plantandprojpaths = [i[0] for i in list(cursor.fetchall())]
                        print('Plant and Projects to delete: ')
                        time.sleep(0.5)
                        for i in plantandprojpaths:
                            print(i)
                        if 'all' in plantselectionscheck or len(plantselectionscheck)>1:
                            userconfirmation = 1
                        else:
                            print('Do you want to delete the above folders?')
                            userconfirmation = userconfirm()
                        if  userconfirmation == 1:
                            aestheticdel(plantandprojpaths)
                            print('\nPlant folder deleted')
                            time.sleep(0.5)
                        elif userconfirmation == 0:
                            print('Skipping deleting Plant and Project folders and proceeding to delete information from site....')
                            time.sleep(0.5)
                        if len(plprojsp_idlist)==1:
                            droploginquery = f"select username from {SiteDatabase}.{SiteSchema}.T_DB_Data where sp_rootitemid = '{tuple(plprojsp_idlist)[0]}'"
                        else:    
                            droploginquery = f"select username from {SiteDatabase}.{SiteSchema}.T_DB_Data where sp_rootitemid in {tuple(plprojsp_idlist)}"
                        cursor.execute(droploginquery)
                        droploginlist = list(cursor.fetchall())
                        print('Dropping logins......')
                        time.sleep(0.5)
                        for i in droploginlist:
                            cursor.execute(f"select name from sys.server_principals where name = '{i[0]}'")
                            loginname = cursor.fetchall()
                            if len(loginname)!=0:
                                cursor.execute(f"use master")
                                cursor.execute(f"drop login {i[0]}")
                        print(f'Logins dropped successfully!')
                        time.sleep(0.5)
                        print("Deleting removed plant's information from the Site tables......")
                        time.sleep(0.5)
                        conn.autocommit= False
                        if len(plprojsp_idlist)==1:
                            rootitemdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_rootitem where sp_id = '{tuple(plprojsp_idlist)[0]}'"
                            db_datadelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_db_data where sp_rootitemid = '{tuple(plprojsp_idlist)[0]}'"
                            rolessp_idquery = f"select sp_id from {SiteDatabase}.{SiteSchema}.t_roles where proj_id = '{tuple(plprojsp_idlist)[0]}'"
                        else:
                            rootitemdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_rootitem where sp_id in {tuple(plprojsp_idlist)}"
                            db_datadelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_db_data where sp_rootitemid in {tuple(plprojsp_idlist)}"
                            rolessp_idquery = f"select sp_id from {SiteDatabase}.{SiteSchema}.t_roles where proj_id in {tuple(plprojsp_idlist)}"
                        cursor.execute(rolessp_idquery)
                        rolessp_idlist = []
                        rolessp_idlist.extend([i[0] for i in list(cursor.fetchall())])
                        rolessp_idlist = tuple(rolessp_idlist)
                        if len(rolessp_idlist)==1:
                            rolesdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_roles where proj_id = '{tuple(plprojsp_idlist)[0]}'"
                            roleusersdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_roleusers where role_id = '{rolessp_idlist[0]}'"
                            rolerightsdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_rolerights where role_id = '{rolessp_idlist[0]}'"
                        else: 
                            rolesdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_roles where proj_id in {tuple(plprojsp_idlist)}"
                            roleusersdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_roleusers where role_id in {rolessp_idlist}"
                            rolerightsdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_rolerights where role_id in {rolessp_idlist}"
                        try:
                            cursor.execute(rootitemdelquery)
                            cursor.execute(db_datadelquery)
                            cursor.execute(rolesdelquery)
                            cursor.execute(roleusersdelquery)
                            cursor.execute(rolerightsdelquery)
                            conn.commit()
                        except pyodbc.DatabaseError as e:
                            print(f'The following error occurred: {e}')
                            conn.rollback()
                        conn.autocommit= True
                        print('\nPlant has been deleted successfully!\n')
                        time.sleep(1)
                conn.close()
            except pyodbc.Error as sqlstate:
                print(f'Failed to connect: {sqlstate.args[0]} - {sqlstate.args[1]}')
            except Exception as e:
                print(f'Exception occured : {e}')
        