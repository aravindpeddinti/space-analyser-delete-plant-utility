import oracledb
import pyodbc
import os
import winreg
import stat
import shutil
import errno
import time
import pwinput


# Function to remove the read-only attribute on the folder run-time.
def handleRemoveReadonly(func, path, exc):
            excvalue = exc[1]
            if func in (os.rmdir, os.remove) and excvalue.errno == errno.EACCES:
                os.chmod(path, stat.S_IRWXU| stat.S_IRWXG| stat.S_IRWXO)
                func(path)
            else:
                raise
# Function to delete the folder
def delfolder(path):
    if os.path.exists(path):
        shutil.rmtree(path, ignore_errors=False,onexc=handleRemoveReadonly)
    else:
        return f'Folder not found: {path}'
    
print('Delete Plant Utility v1')
time.sleep(1)

inipath = input("Give the smartplantv4.ini Path: ").strip('"')
time.sleep(0.5)
with open(inipath, 'r', encoding='utf-16') as file:
    lines = file.readlines()
SiteSchema = lines[5].split('=')[1].strip()
dbtype = lines[2].split('=')[1].strip()

if dbtype=='ORACLE':
    # Reading the ini file to get the oracle service name and site schema
    
    Oracle_Instance = lines[4].split('=')[1].strip()
    username = input('Enter system username: ')
    password = pwinput.pwinput(prompt='Enter system password: ', mask='*')

    #Finding out the 32 bit oracle client on the machine to extract the tns names from the tnsnames.ora file
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
    if not oracle_paths:
        raise RuntimeError("No Oracle clients found.")

    #Setting the oracle_home path to get the tnsnames.ora file
    oracle_home = oracle_paths[0]
    print(f'Oracle 32-bit client home found at {oracle_home}')
    

    #Getting the tnsnames.ora file from the oracle_home path
    def get_tnsnames_path(oracle_home):
        tnsnames_path = os.path.join(oracle_home, 'network', 'admin', 'tnsnames.ora')
        return tnsnames_path

    tnsnames_path = get_tnsnames_path(oracle_home)
    tnsnames_dir = os.path.dirname(tnsnames_path)


    if not os.path.exists(tnsnames_path):
        raise RuntimeError("tnsnames.ora file not found.")
    
    time.sleep(0.5)
    print(f"Connecting to Oracle instance {Oracle_Instance} as {username}")
    # Connecting to the oracle service using the service name from the ini file.
    try:
        oracledb.init_oracle_client(config_dir=tnsnames_dir)
        connection = oracledb.connect(
            user=username,
            password=password,
            dsn=Oracle_Instance,
            config_dir=tnsnames_dir
        )
        cursor = connection.cursor()
        #Changing the default schema to the site schema (Not required)
        query = f"alter session set current_schema = {SiteSchema}"
        cursor.execute(query)
        # Getting the list of plant names in the site
        query1 = f"select sp_id, Name, sp_parentrootitemid from T_RootItem where SP_ParentRootItemID ='0' or sp_parentrootitemid is null"
        cursor.execute(query1)
        plantnamecursor = cursor.fetchall()
        print('List of plants available in the site: ')
        time.sleep(0.5)
        for sp_id, Name, parentrootitemid in plantnamecursor:
            print(Name)
        #select the plant name
        time.sleep(0.5)
        plantselection = int(input('Select Plant (1 for first plant, 2 for second plant..)'))
        # Getting the list of plant and project sp_ids for the selected plant.
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
        plantschemaquery = f"select username from t_db_data where sp_rootitemid in {projectsp_idlist}"
        cursor.execute(plantschemaquery)
        plantschemacursor = cursor.fetchall()
        plantschemalist = [i[0] for i in list(plantschemacursor)]
        print(f'List of schemas to drop: {plantschemalist}')
        time.sleep(0.5)
        # getting the list of schemas from which we can get the reference data path for each plant
        appusernamequery = f"select username from t_db_data where sp_rootitemid in {projectsp_idlist} and sp_schema_type in ('SPEL','SPPID')"
        cursor.execute(appusernamequery)
        appusernamelist = list(cursor.fetchall())
        # getting the ref data paths
        pidrefvaluepathlist = []
        for i in appusernamelist:
            pidvaluepathquery = f"select value from {i[0]}.t_optionsetting where sp_id in ('680','681','682','683','684','685','703','709','804','809','824')"
            cursor.execute(pidvaluepathquery)
            pidrefvaluepathlist.extend([j[0] for j in list(cursor.fetchall())])
        elrefvaluepathlist = []
        for i in appusernamelist:
            elvaluepathquery = f"select value from {i[0]}.t_optionsetting where sp_id in ('1001','1002','1004','1005','1008','1010','1011','1018','1022','1023','1024','1034','1035','1040','1047','1048','1049','1050','1051','1052','1054','1055','1061','1063','1074','1075','1076')"
            cursor.execute(elvaluepathquery)
            elrefvaluepathlist.extend([j[0] for j in list(cursor.fetchall())])
        
        """
        This is old one - doesn't work
        # for path in valuepathlist:
        #     if os.path.exists(path):
        #         try:
        #             # If the file is read-only, change permissions to allow deletion
        #             os.chmod(path, stat.S_IWRITE)
        #             os.remove(path)
        #             print(f"Removed: {path}")
        #         except PermissionError:
        #             print(f"Permission denied: {path}")
        #     else:
        #         print(f"File not found: {path}")
        """
        # creating a common path for all the ref data paths and deleting them.
        if pidrefvaluepathlist:
            pidcommon_refvaluepath = os.path.commonpath(pidrefvaluepathlist)
            print(f"P&ID Reference Data folder to get deleted: {pidcommon_refvaluepath}")
            time.sleep(0.5)
            # delfolder(pidcommon_refvaluepath)
        if elrefvaluepathlist: 
            elcommon_refvaluepath = os.path.commonpath(elrefvaluepathlist)
            print(f"SEL Reference Data folder to get deleted: {elcommon_refvaluepath}")
            time.sleep(0.5)
            # delfolder(elcommon_refvaluepath)
        
        
        

        # Getting the schemas of only the projects to get the project folder
        projectfolderschemaquery = f"select username from t_db_data where sp_rootitemid in {justprojectsp_ids} and sp_schema_type = 'SPAPLANT'"
        cursor.execute(projectfolderschemaquery)
        projectfolderschema = [i[0] for i in list(cursor.fetchall())]
        #getting the list of project folders
        projectfolderpaths = []
        for i in projectfolderschema:
            projectfolderquery = f"select value from {i}.t_plantsetting where sp_id = '711'"
            cursor.execute(projectfolderquery)
            projectfolderpaths.extend([j[0] for j in list(cursor.fetchall())])
        # deleting the project folders
        print(f"List of Project folders to get deleted: ")
        for i in projectfolderpaths:
            print(i)
            # delfolder(i)
        time.sleep(0.5)
        
        # getting the schemas of only the plants.
        plantfolderschemaquery = f"select username from t_db_data where sp_rootitemid = '{plantsp_id}' and sp_schema_type = 'SPAPLANT'"
        cursor.execute(plantfolderschemaquery)
        plantfolderschema = [i[0] for i in list(cursor.fetchall())]
        # getting the plant folder
        plantfolderpath = []
        for i in plantfolderschema:
            plantfolderquery = f"select value from {i}.t_plantsetting where sp_id = '711'"
            cursor.execute(plantfolderquery)
            plantfolderpath.extend([j[0] for j in list(cursor.fetchall())])
        # deleting the plant folder
        print('Plant folder to get deleted: ')
        time.sleep(0.5)
        for i in plantfolderpath:
            print(i)
            # delfolder(i)
        userconfirmation = int(input('Are you sure you want to delete the plant and the projects?\nThis will delete the plant and project schemas and removes the physical files.\nThis step is irreversible.\n(Select 1 or Yes and 0 or No) '))
        if userconfirmation:
            time.sleep(0.5)
            if pidrefvaluepathlist:
                print('Deleting P&ID Reference Data folder........')
                delfolder(pidcommon_refvaluepath)
                time.sleep(0.5)
            if elrefvaluepathlist:
                print('Deleting SEL Reference Data folder......')
                delfolder(elcommon_refvaluepath)
                time.sleep(0.5)
            print('Deleting Project Folders....')
            for i in projectfolderpaths:
                delfolder(i)
            time.sleep(0.5)
            print('Deleting Plant folder.....')
            for i in plantfolderpath:
                delfolder(i)
            time.sleep(0.5)
            print('Dropping the database....')
            time.sleep(0.5)
            # dropping all the schemas
            for i in plantschemalist:
                dropschemaquery = f"drop user {i} cascade"
                cursor.execute(dropschemaquery)
            print('Database schema and physical files are deleted successfully!')
            time.sleep(0.5)
            print("Deleting removed plant's information from the Site tables......")
            time.sleep(0.5)
            rootitemdelquery = f"delete from t_rootitem where sp_id in {projectsp_idlist}"
            db_datadelquery = f"delete from t_db_data where sp_rootitemid in {projectsp_idlist}"
            rolessp_idquery = f"select sp_id from t_roles where proj_id in {projectsp_idlist}"

            cursor.execute(rolessp_idquery)
            rolessp_idlist = []
            rolessp_idlist.extend([i[0] for i in list(cursor.fetchall())])
            rolessp_idlist = tuple(rolessp_idlist)
            rolesdelquery = f"delete from t_roles where proj_id in {projectsp_idlist}"
            roleusersdelquery = f"delete from t_roleusers where role_id in {rolessp_idlist}"
            rolerightsdelquery = f"delete from t_rolerights where role_id in {rolessp_idlist}"
            try:
                cursor.execute(rootitemdelquery)
                cursor.execute(db_datadelquery)
                cursor.execute(rolesdelquery)
                cursor.execute(roleusersdelquery)
                cursor.execute(rolerightsdelquery)
                connection.commit()
            except oracledb.DatabaseError as e:
                print(f'The following error occurred: {e}')
                connection.rollback()
            print('Plant has been deleted successfully!')   
            time.sleep(1)
        
        cursor.close()
        connection.close()

    except oracledb.DatabaseError as e:
        print(f"Database error occurred: {e}")

    except Exception as e:
        print(f"An error occurred: {e}")
else:
    
    SiteDatabase = lines[4].split('=')[1].strip()
 
    SQLServer = lines[3].split('=')[1].strip()
    username = input('Enter SQL Authentication username: ')
    password = pwinput.pwinput(prompt='Enter SQL Authentication password: ', mask='*')
    driver = '{SQL Server Native Client 11.0}'
    masterdb = 'master'
    time.sleep(0.5)
    print(f"Connecting to SQL Server at {SQLServer} as {username}")
    try:
        connection_string = f"DRIVER={driver};SERVER={SQLServer};DATABASE={masterdb};UID={username};PWD={password}"
        conn = pyodbc.connect(connection_string,autocommit=True)
        cursor = conn.cursor()
        # Getting the list of plant names in the site
        cursor.execute(f"select sp_id, Name from {SiteDatabase}.{SiteSchema}.T_RootItem where SP_ParentRootItemID ='0' or sp_parentrootitemid is null")
        plantnamecursor = cursor.fetchall()
        print('List of Plants available in the site: ')
        time.sleep(0.5)
        for sp_id, Name in plantnamecursor:
            print(Name)
        #select the plant name
        plantselection = int(input('Select Plant (1 for first plant, 2 for second plant..) '))
        time.sleep(0.5)
        # Getting the list of plant and project sp_ids for the selected plant.
        plantrootitemid = list(plantnamecursor)[plantselection-1][0]
        cursor.execute(f"select name from {SiteDatabase}.{SiteSchema}.t_rootitem where sp_id = '{plantrootitemid}'")
        print(f'Plant selected to delete: {cursor.fetchall()[0][0]}')
        time.sleep(0.5)
        projectschemaquery = f"select sp_id from {SiteDatabase}.{SiteSchema}.t_rootitem where sp_parentrootitemid = '{plantrootitemid}'"
        cursor.execute(projectschemaquery)
        plprojsp_idlist = [i[0] for i in list(cursor.fetchall())]
        justprojsp_ids = tuple(plprojsp_idlist)
        plprojsp_idlist.append(plantrootitemid)

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
            pidcommonrefdatapath = os.path.commonpath(pidrefdatapathslist)
            print(f"P&ID Reference Data folder to get deleted: {pidcommonrefdatapath}")
            time.sleep(0.5)
            # delfolder(pidcommonrefdatapath)
        if elrefdatapathslist:
            elcommonrefdatapath = os.path.commonpath(elrefdatapathslist)
            print(f"SEL Reference Data folder to get deleted: {elcommonrefdatapath}")
            time.sleep(0.5)
            # delfolder(elcommonrefdatapath)
        
        projdbandschemaquery = f"select username,data_base from {SiteDatabase}.{SiteSchema}.T_DB_DATA where sp_rootitemid in {tuple(justprojsp_ids)} and sp_schema_type = 'SPAPLANT'"
        cursor.execute(projdbandschemaquery)
        projdbandschemalist = list(cursor.fetchall())
        projdblist = [i[1] for i in projdbandschemalist]
        projschemalist = [i[0] for i in projdbandschemalist]
        projfolderlist = []
        for i,j in zip(projdblist,projschemalist):
            projfolderquery = f"select value from {i}.{j}.t_plantsetting where sp_id = '711'"
            cursor.execute(projfolderquery)
            projfolderlist.extend([k[0] for k in list(cursor.fetchall())])
        print(f"Projects folders to get deleted: ")
        time.sleep(0.5)
        for i in projfolderlist:
            print(i)
            # delfolder(i)
        time.sleep(0.5)
        plantdbandschemaquery = f"select username, data_base from {SiteDatabase}.{SiteSchema}.T_DB_DATa where sp_rootitemid = '{plantrootitemid}' and sp_schema_type = 'SPAPLANT'"
        cursor.execute(plantdbandschemaquery)
        plantdbandschemalist = cursor.fetchall()
        plantdb = plantdbandschemalist[0][1]
        plantschema = plantdbandschemalist[0][0]
        plantfolderquery = f"select value from {plantdb}.{plantschema}.t_plantsetting where sp_id ='711'"
        cursor.execute(plantfolderquery)
        plantfolderpath = cursor.fetchall()[0][0]
        print(f"Plant folder to get deleted: {plantfolderpath}")
        # delfolder(plantfolderpath)

        dropdbquery = f"select distinct data_base from {SiteDatabase}.{SiteSchema}.t_db_data where sp_rootitemid in {tuple(plprojsp_idlist)}"
        cursor.execute(dropdbquery)
        dropdblist = list(cursor.fetchall())[0]
        print(f"Database(s) to drop: ")
        time.sleep(0.5)
        for i in dropdblist:
            print(i)
        time.sleep(0.5)
        userconfirmation = int(input('Are you sure you want to delete the plant and the projects?\nThis will delete the plant database and removes the physical files.\nThis step is irreversible.\n(Select 1 or Yes and 0 or No) '))
        if userconfirmation:
            time.sleep(0.5)
            if pidrefdatapathslist:
                print('Deleting P&ID Reference Data folder........')
                delfolder(pidcommonrefdatapath)
                time.sleep(0.5)
            if elrefdatapathslist:
                print('Deleting SEL Reference Data folder......')
                delfolder(elcommonrefdatapath)
                time.sleep(0.5)
            print('Deleting Project Folders....')
            time,slice(0.5)
            for i in projfolderlist:
                delfolder(i)
            print('Deleting Plant folder.....')
            time.sleep(0.5)
            delfolder(plantfolderpath)
            print('Dropping the database....')
            time.sleep(0.5)
            for i in dropdblist:
                
                try:
                    cursor.execute(f"exec msdb.dbo.sp_delete_database_backuphistory @database_name = N'{i}'")                
                    terminate_query = f"ALTER DATABASE {i} SET SINGLE_USER WITH ROLLBACK IMMEDIATE"
                    cursor.execute(terminate_query)
                    print(f"Terminated active connections to {i}")    
                    time.sleep(0.5)            
                    cursor.execute('use master')
                    cursor.execute(f"DROP DATABASE {i}")
                    print(f'Database {i} dropped successfully!')
                    time.sleep(0.5)
        
                except pyodbc.Error as ex:
                    sqlstate = ex.args[0]
                    print(f'Failed to execute SQL command. SQLSTATE: {sqlstate}')
                except Exception as e:
                    print(f'Exception occurred: {e}')        
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
            rootitemdelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_rootitem where sp_id in {tuple(plprojsp_idlist)}"
            db_datadelquery = f"delete from {SiteDatabase}.{SiteSchema}.t_db_data where sp_rootitemid in {tuple(plprojsp_idlist)}"
            rolessp_idquery = f"select sp_id from {SiteDatabase}.{SiteSchema}.t_roles where proj_id in {tuple(plprojsp_idlist)}"
            cursor.execute(rolessp_idquery)
            rolessp_idlist = []
            rolessp_idlist.extend([i[0] for i in list(cursor.fetchall())])
            rolessp_idlist = tuple(rolessp_idlist)
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
            print('Plant has been deleted successfully!')
            time.sleep(1)

        conn.close()
    except pyodbc.Error as ex:
        sqlstate = ex.args[0]
        print(f'Failed to connect: {sqlstate}')
    except Exception as e:
        print(f'Exception occured : {e}')
    